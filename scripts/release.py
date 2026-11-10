#!/usr/bin/env python3
"""Rebase/release automation for jellyfin-oidc. See DESIGN.md and README.md.

Operates on a git checkout of the fork (patch branches, upstream history),
given with --repo. The automation files (Dockerfile, e2e/) are taken from the
directory this script lives in. Stdlib only.

Subcommands (run `release.py <cmd> -h` for options):

  plan      decide what to do for an upstream version (default: latest stable)
  replay    create oidc/v<new> from upstream tag v<new> and cherry-pick the
            patch series of the previous oidc/v<old> branch onto it
  continue  after resolving a replay conflict by hand: continue the series
  finalize  make sure the branch carries no upstream .github (regenerated
            commit with the fork's CI stub instead)
  gate      build the image, run the fork's unit tests and the e2e suite
  publish   push branch + tag, push image tags, create the GitHub release
  run       plan + replay + gate (+ publish unless --no-publish)

Exit codes (stable, for CI):
  0  success (also: nothing to do, or waiting for the upstream image)
  2  usage error
  3  replay stopped on a conflict; see <state-dir>/conflict.json
  4  image build failed
  5  unit tests failed (or no fork unit tests found)
  6  e2e gate failed
  7  publish failed
  8  precondition / invalid state (dirty tree, missing refs, untested image, ...)
  9  e2e harness could not run (infrastructure)
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_CONFLICT = 3
EXIT_BUILD = 4
EXIT_UNIT = 5
EXIT_E2E = 6
EXIT_PUBLISH = 7
EXIT_STATE = 8
EXIT_INFRA = 9

AUTOMATION_DIR = Path(__file__).resolve().parent.parent
STUB_TEMPLATE = AUTOMATION_DIR / "scripts" / "branch-ci.yml.in"
MARKER_TRAILER = "Jellyfin-Oidc-Automation"
MARKER_VALUE = "ci-stub"
STABLE_TAG_RE = re.compile(r"^v(\d+)\.(\d+)(?:\.(\d+))?$")
FORK_TAG_RE = re.compile(r"^v(\d+\.\d+(?:\.\d+)?)-oidc\.(\d+)$")
BRANCH_RE = re.compile(r"^oidc/v(\d+\.\d+(?:\.\d+)?)$")

# Never depend on the invoking user's signing setup (CI has none; the owner's
# laptop signs with a hardware key that needs a touch).
GIT_NOSIGN = ["-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"]


class Fail(Exception):
    def __init__(self, code: int, msg: str):
        super().__init__(msg)
        self.code = code


def log(msg: str) -> None:
    print(f"[release] {msg}", file=sys.stderr, flush=True)


def vtuple(v: str) -> tuple[int, ...]:
    return tuple(int(x) for x in v.lstrip("v").split("."))


# --------------------------------------------------------------------- git


@dataclass
class Ctx:
    repo: Path
    origin: str
    upstream: str
    upstream_url: str
    state_dir: Path
    image_repo: str
    github_repo: str
    dry_run: bool = False

    def git(self, *args: str, check: bool = True, capture: bool = True, env: dict | None = None,
            input: str | None = None) -> subprocess.CompletedProcess:
        e = dict(os.environ)
        e.setdefault("GIT_EDITOR", "true")
        e.setdefault("GIT_TERMINAL_PROMPT", "0")
        if env:
            e.update(env)
        cmd = ["git", "-C", str(self.repo), *GIT_NOSIGN, *args]
        r = subprocess.run(cmd, text=True, capture_output=capture, env=e, input=input)
        if check and r.returncode != 0:
            raise Fail(EXIT_STATE, f"git {' '.join(args)} failed ({r.returncode}): {(r.stderr or '').strip()}")
        return r

    def out(self, *args: str) -> str:
        return self.git(*args).stdout.strip()

    def ref_exists(self, ref: str) -> bool:
        return self.git("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}", check=False).returncode == 0

    def sha(self, ref: str) -> str:
        return self.out("rev-parse", f"{ref}^{{commit}}")

    def path(self, name: str) -> Path:
        self.state_dir.mkdir(parents=True, exist_ok=True)
        return self.state_dir / name


def ensure_identity(ctx: Ctx) -> None:
    """Cherry-picks need a committer; CI checkouts have none configured."""
    if ctx.git("config", "user.email", check=False).returncode != 0:
        os.environ.setdefault("GIT_COMMITTER_NAME", "jellyfin-oidc automation")
        os.environ.setdefault("GIT_COMMITTER_EMAIL", "jellyfin-oidc-automation@users.noreply.github.com")
        os.environ.setdefault("GIT_AUTHOR_NAME", os.environ["GIT_COMMITTER_NAME"])
        os.environ.setdefault("GIT_AUTHOR_EMAIL", os.environ["GIT_COMMITTER_EMAIL"])


def sequencer_in_progress(ctx: Ctx) -> bool:
    gd = Path(ctx.out("rev-parse", "--absolute-git-dir"))
    return (gd / "CHERRY_PICK_HEAD").exists() or (gd / "sequencer").exists()


def worktree_dirty(ctx: Ctx) -> bool:
    return ctx.out("status", "--porcelain", "--untracked-files=no") != ""


def fetch(ctx: Ctx, version: str | None) -> None:
    remotes = ctx.out("remote").split()
    if ctx.upstream not in remotes:
        log(f"adding remote {ctx.upstream} -> {ctx.upstream_url}")
        ctx.git("remote", "add", "--no-tags", ctx.upstream, ctx.upstream_url)
    if ctx.origin in remotes:
        log(f"fetching {ctx.origin}")
        ctx.git("fetch", "--prune", "--tags", ctx.origin,
                f"+refs/heads/oidc/*:refs/remotes/{ctx.origin}/oidc/*")
    else:
        log(f"warning: no remote '{ctx.origin}'; treating the fork as having no published branches")
    # Upstream stable tags only (no RCs); fetched as tags so v<old>/v<new> resolve.
    refspecs = ["+refs/tags/v*:refs/tags/v*"] if version is None else [f"+refs/tags/v{version}:refs/tags/v{version}"]
    log(f"fetching upstream tags from {ctx.upstream}")
    r = ctx.git("fetch", "--no-tags", ctx.upstream, *refspecs, check=False)
    if r.returncode != 0:
        raise Fail(EXIT_STATE, f"fetching upstream tags failed: {r.stderr.strip()}")


def remote_fork_tags(ctx: Ctx) -> dict[str, str]:
    """Fork release tags on origin: name -> commit sha."""
    if ctx.origin not in ctx.out("remote").split():
        return {}
    r = ctx.git("ls-remote", "--tags", ctx.origin, "refs/tags/v*-oidc.*", check=False)
    if r.returncode != 0:
        raise Fail(EXIT_STATE, f"git ls-remote {ctx.origin} failed: {r.stderr.strip()}")
    tags: dict[str, str] = {}
    for line in r.stdout.splitlines():
        sha, ref = line.split("\t")
        name = ref.removeprefix("refs/tags/")
        if name.endswith("^{}"):
            tags[name[:-3]] = sha  # peeled annotated tag -> commit
        else:
            tags.setdefault(name, sha)
    return {k: v for k, v in tags.items() if FORK_TAG_RE.match(k)}


def origin_branches(ctx: Ctx) -> dict[str, str]:
    """version -> remote-tracking ref, for oidc/v* branches on origin."""
    out = ctx.out("for-each-ref", "--format=%(refname)", f"refs/remotes/{ctx.origin}/oidc/")
    res = {}
    for ref in out.splitlines():
        m = BRANCH_RE.match(ref.removeprefix(f"refs/remotes/{ctx.origin}/"))
        if m:
            res[m.group(1)] = ref
    return res


def automation_commits(ctx: Ctx, rng: str) -> set[str]:
    out = ctx.out("log", "--format=%H", f"--grep=^{MARKER_TRAILER}: {MARKER_VALUE}$", rng)
    return set(out.split())


def patch_series(ctx: Ctx, base: str, head: str) -> list[str]:
    rng = f"{base}..{head}"
    merges = ctx.out("rev-list", "--merges", rng)
    if merges:
        raise Fail(EXIT_STATE, f"patch series {rng} contains merge commits ({merges.split()[0][:10]}...); it must be linear")
    skip = automation_commits(ctx, rng)
    return [c for c in ctx.out("rev-list", "--reverse", rng).split() if c and c not in skip]


# --------------------------------------------------------------- upstream


def http_json(url: str) -> object:
    req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json",
                                               "User-Agent": "jellyfin-oidc-release-script"})
    tok = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if tok and url.startswith("https://api.github.com/"):
        req.add_header("Authorization", f"Bearer {tok}")
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def latest_upstream_stable() -> str:
    rels = http_json("https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=50")
    best: tuple[int, ...] | None = None
    best_tag = ""
    for rel in rels:  # type: ignore[union-attr]
        tag = rel.get("tag_name", "")
        if rel.get("draft") or rel.get("prerelease") or not STABLE_TAG_RE.match(tag):
            continue
        t = vtuple(tag)
        if best is None or t > best:
            best, best_tag = t, tag
    if not best_tag:
        raise Fail(EXIT_STATE, "no stable upstream release found")
    return best_tag.lstrip("v")


def base_image_exists(version: str) -> bool:
    r = subprocess.run(["docker", "buildx", "imagetools", "inspect", f"docker.io/jellyfin/jellyfin:{version}"],
                       capture_output=True, text=True)
    return r.returncode == 0


def github_release_exists(ctx: Ctx, tag: str) -> bool:
    try:
        http_json(f"https://api.github.com/repos/{ctx.github_repo}/releases/tags/{tag}")
        return True
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return False
        raise Fail(EXIT_STATE, f"GitHub API error checking release {tag}: {e}")


# ------------------------------------------------------------------- plan


def cmd_plan(ctx: Ctx, a: argparse.Namespace) -> int:
    if a.branch:
        m = BRANCH_RE.match(a.branch)
        if not m:
            raise Fail(EXIT_USAGE, f"--branch must look like oidc/v12.4, got {a.branch}")
        version = m.group(1)
    elif a.version:
        version = a.version.lstrip("v")
        if not STABLE_TAG_RE.match(f"v{version}"):
            raise Fail(EXIT_USAGE, f"not a stable version: {a.version}")
    else:
        version = None

    if not a.no_fetch:
        fetch(ctx, None)
    if version is None:
        version = latest_upstream_stable()
        log(f"latest upstream stable release: v{version}")

    upstream_tag = f"v{version}"
    branch = f"oidc/v{version}"
    plan: dict = {"version": version, "upstream_tag": upstream_tag, "branch": branch,
                  "image_repo": ctx.image_repo, "github_repo": ctx.github_repo}

    def done(action: str, reason: str, **kw) -> int:
        plan.update(action=action, reason=reason, **kw)
        ctx.path("plan.json").write_text(json.dumps(plan, indent=2) + "\n")
        for name in ("conflict.json", "gate.json"):
            p = ctx.state_dir / name
            if p.exists():
                p.unlink()
        log(f"plan: {action}: {reason}")
        print(json.dumps(plan, indent=2))
        gh_output(plan)
        return EXIT_OK

    if sequencer_in_progress(ctx):
        raise Fail(EXIT_CONFLICT, "a cherry-pick is in progress in the repo; resolve it and run `continue`, "
                                  "or abort it with `git cherry-pick --abort`")
    if not ctx.ref_exists(upstream_tag):
        raise Fail(EXIT_STATE, f"upstream tag {upstream_tag} not found (fetch failed or not released)")
    if not a.skip_image_check and not base_image_exists(version):
        return done("wait", f"jellyfin/jellyfin:{version} is not on Docker Hub yet")

    tags = remote_fork_tags(ctx)
    mine = sorted(((int(FORK_TAG_RE.match(t).group(2)), t, s) for t, s in tags.items()  # type: ignore[union-attr]
                   if FORK_TAG_RE.match(t).group(1) == version))  # type: ignore[union-attr]
    branches = origin_branches(ctx)
    newest_published = max([vtuple(v) for v in branches] or [(0,)])
    plan["latest"] = vtuple(version) >= newest_published

    remote_ref = branches.get(version)
    local_ref = f"refs/heads/{branch}"
    if remote_ref:
        head = ctx.sha(remote_ref)
        if ctx.ref_exists(local_ref) and ctx.sha(local_ref) != head:
            ahead = ctx.out("rev-list", "--count", f"{remote_ref}..{local_ref}")
            behind = ctx.out("rev-list", "--count", f"{local_ref}..{remote_ref}")
            if behind != "0":
                raise Fail(EXIT_STATE, f"local {branch} is not a fast-forward of {remote_ref} "
                                       f"(ahead {ahead}, behind {behind}); fix it by hand")
            log(f"local {branch} is {ahead} commit(s) ahead of {ctx.origin}; releasing the local branch")
            head = ctx.sha(local_ref)
        if mine and mine[-1][2] == head:
            n, tag, _ = mine[-1]
            if a.skip_release_check or github_release_exists(ctx, tag):
                return done("noop", f"{tag} already released at {head[:10]}", fork_tag=tag, n=n, head=head)
            return done("release", f"{tag} is tagged but has no GitHub release; finishing it",
                        n=n, fork_tag=tag, fork_version=tag[1:], source="existing", head=head)
        n = (mine[-1][0] + 1) if mine else 1
        if mine:
            reason = f"{branch} has commits after {mine[-1][1]}"
        else:
            reason = f"{branch} exists but was never released"
        if not ctx.ref_exists(local_ref):
            ctx.git("branch", branch, head)
        return done("release", reason, n=n, fork_tag=f"v{version}-oidc.{n}", fork_version=f"{version}-oidc.{n}",
                    source="existing", head=head)

    if a.branch:
        if ctx.ref_exists(local_ref):
            return done("release", f"{branch} exists locally only", n=1, fork_tag=f"v{version}-oidc.1",
                        fork_version=f"{version}-oidc.1", source="local", head=ctx.sha(local_ref))
        raise Fail(EXIT_STATE, f"{branch} does not exist")

    if ctx.ref_exists(local_ref) and not a.fresh:
        return done("release", f"{branch} exists locally only (an earlier replay); use --fresh to redo it",
                    n=1, fork_tag=f"v{version}-oidc.1", fork_version=f"{version}-oidc.1", source="local",
                    head=ctx.sha(local_ref))

    older = sorted((vtuple(v), v) for v in branches if vtuple(v) < vtuple(version))
    if not older:
        raise Fail(EXIT_STATE, f"no published oidc/v* branch older than v{version} to replay from")
    prev = older[-1][1]
    prev_ref = branches[prev]
    if not ctx.ref_exists(f"v{prev}"):
        raise Fail(EXIT_STATE, f"upstream tag v{prev} (base of oidc/v{prev}) not found")
    series = patch_series(ctx, f"v{prev}", prev_ref)
    if not series:
        raise Fail(EXIT_STATE, f"oidc/v{prev} has no patch commits on top of v{prev}")
    return done("replay", f"new upstream release v{version}; replaying {len(series)} commit(s) from oidc/v{prev}",
                n=1, fork_tag=f"v{version}-oidc.1", fork_version=f"{version}-oidc.1", source="replay",
                prev_version=prev, prev_branch=f"oidc/v{prev}", prev_ref=prev_ref, series=series)


def gh_output(plan: dict) -> None:
    out = os.environ.get("GITHUB_OUTPUT")
    if not out:
        return
    with open(out, "a") as f:
        for k in ("action", "reason", "version", "branch", "fork_tag", "fork_version", "n", "prev_branch", "latest"):
            if k in plan:
                v = plan[k]
                f.write(f"{k}={json.dumps(v) if isinstance(v, bool) else v}\n")


def load_plan(ctx: Ctx) -> dict:
    p = ctx.state_dir / "plan.json"
    if not p.exists():
        raise Fail(EXIT_STATE, f"no plan at {p}; run `plan` first")
    return json.loads(p.read_text())


# ----------------------------------------------------------------- replay


def conflict_state(ctx: Ctx, plan: dict, note: str = "") -> dict:
    gd = Path(ctx.out("rev-parse", "--absolute-git-dir"))
    cur = ""
    if (gd / "CHERRY_PICK_HEAD").exists():
        cur = (gd / "CHERRY_PICK_HEAD").read_text().strip()
    files = [f for f in ctx.out("diff", "--name-only", "--diff-filter=U").splitlines() if f]
    series = plan.get("series", [])
    idx = series.index(cur) if cur in series else None
    remaining = []
    todo = gd / "sequencer" / "todo"
    if todo.exists():
        for line in todo.read_text().splitlines():
            parts = line.split()
            if len(parts) >= 2 and parts[0] in ("pick", "p"):
                remaining.append(ctx.out("rev-parse", parts[1]))
    if remaining and cur and remaining[0] == cur:
        remaining = remaining[1:]  # the todo list starts with the commit that stopped
    st = {
        "status": "conflict",
        "branch": plan["branch"],
        "onto": plan["upstream_tag"],
        "from_branch": plan.get("prev_branch"),
        "commit": cur,
        "commit_subject": ctx.out("log", "-1", "--format=%s", cur) if cur else "",
        "commit_index": (idx + 1) if idx is not None else None,
        "series_length": len(series),
        "conflicted_files": files,
        "remaining_after_current": remaining,
        "note": note,
        "resolve_with": "edit the files, `git add` them, then `release.py continue`",
    }
    ctx.path("conflict.json").write_text(json.dumps(st, indent=2) + "\n")
    return st


def cmd_replay(ctx: Ctx, a: argparse.Namespace) -> int:
    plan = load_plan(ctx)
    if plan["action"] != "replay":
        log(f"plan action is '{plan['action']}', nothing to replay")
        return EXIT_OK
    if sequencer_in_progress(ctx):
        raise Fail(EXIT_CONFLICT, "a cherry-pick is already in progress; use `continue`")
    if worktree_dirty(ctx):
        raise Fail(EXIT_STATE, "working tree has uncommitted changes")
    ensure_identity(ctx)
    branch = plan["branch"]
    if ctx.ref_exists(f"refs/heads/{branch}"):
        if not a.fresh:
            raise Fail(EXIT_STATE, f"{branch} already exists locally; pass --fresh to recreate it")
        cur = ctx.git("symbolic-ref", "--short", "-q", "HEAD", check=False).stdout.strip()
        if cur == branch:
            ctx.git("checkout", "-q", "--detach")
        ctx.git("branch", "-D", branch)
    log(f"creating {branch} from {plan['upstream_tag']}")
    ctx.git("checkout", "-q", "-b", branch, plan["upstream_tag"])
    series = plan["series"]
    log(f"cherry-picking {len(series)} commit(s) from {plan['prev_branch']}")
    # --empty=drop: a patch commit whose change upstream already contains
    # disappears instead of stopping the sequence (listed by `finalize`).
    r = ctx.git("cherry-pick", "--empty=drop", *series, check=False)
    if r.returncode != 0:
        if sequencer_in_progress(ctx):
            st = conflict_state(ctx, plan)
            log(f"CONFLICT applying {st['commit'][:10]} ({st['commit_subject']}); "
                f"files: {', '.join(st['conflicted_files']) or '(none)'}")
            print(json.dumps(st, indent=2))
            return EXIT_CONFLICT
        raise Fail(EXIT_STATE, f"cherry-pick failed: {r.stderr.strip()}")
    return finalize(ctx, plan)


def cmd_continue(ctx: Ctx, a: argparse.Namespace) -> int:
    plan = load_plan(ctx)
    ensure_identity(ctx)
    while sequencer_in_progress(ctx):
        unmerged = ctx.out("diff", "--name-only", "--diff-filter=U")
        if unmerged:
            st = conflict_state(ctx, plan)
            log(f"still unresolved: {unmerged.replace(chr(10), ', ')}")
            print(json.dumps(st, indent=2))
            return EXIT_CONFLICT
        r = ctx.git("cherry-pick", "--continue", check=False)
        if r.returncode == 0:
            continue
        if "previous cherry-pick is now empty" in (r.stderr + r.stdout):
            log("resolved commit is empty (already upstream); skipping it")
            ctx.git("cherry-pick", "--skip")
            continue
        if sequencer_in_progress(ctx) and ctx.out("diff", "--name-only", "--diff-filter=U"):
            st = conflict_state(ctx, plan)
            log(f"CONFLICT applying {st['commit'][:10]} ({st['commit_subject']})")
            print(json.dumps(st, indent=2))
            return EXIT_CONFLICT
        raise Fail(EXIT_STATE, f"git cherry-pick --continue failed: {(r.stderr or r.stdout).strip()}")
    p = ctx.state_dir / "conflict.json"
    if p.exists():
        p.unlink()
    cur = ctx.git("symbolic-ref", "--short", "-q", "HEAD", check=False).stdout.strip()
    if cur != plan["branch"]:
        raise Fail(EXIT_STATE, f"expected to be on {plan['branch']}, on '{cur or 'detached HEAD'}'")
    return finalize(ctx, plan)


def render_stub(ctx: Ctx) -> str:
    return STUB_TEMPLATE.read_text().replace("@GITHUB_REPO@", ctx.github_repo)


def finalize(ctx: Ctx, plan: dict) -> int:
    """Drop upstream .github in one regenerated commit carrying the fork's CI stub.

    The commit is never cherry-picked (replay skips it by its trailer), so
    upstream changes to .github can never conflict with the patch series."""
    branch = plan["branch"]
    if sequencer_in_progress(ctx):
        raise Fail(EXIT_CONFLICT, "cherry-pick in progress")
    if worktree_dirty(ctx):
        raise Fail(EXIT_STATE, "working tree has uncommitted changes")
    ensure_identity(ctx)
    cur = ctx.git("symbolic-ref", "--short", "-q", "HEAD", check=False).stdout.strip()
    if cur != branch:
        ctx.git("checkout", "-q", branch)
    stub = render_stub(ctx)
    stub_path = ".github/workflows/ci.yml"
    tracked = [f for f in ctx.out("ls-files", "-z", "--", ".github").split("\0") if f]
    current_stub = ctx.git("show", f"HEAD:{stub_path}", check=False)
    if tracked == [stub_path] and current_stub.returncode == 0 and current_stub.stdout == stub:
        log(".github already holds only the current CI stub")
    else:
        if tracked:
            ctx.git("rm", "-r", "-q", "--", ".github")
        dest = ctx.repo / stub_path
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(stub)
        ctx.git("add", "--", stub_path)
        msg = ("Replace upstream .github with the fork's CI stub\n\n"
               "Generated by scripts/release.py on the automation branch. Upstream's\n"
               "workflows must not run in this repository. This commit is regenerated\n"
               "after every replay and never cherry-picked, so upstream changes to\n"
               ".github cannot conflict with the patch series.\n\n"
               f"{MARKER_TRAILER}: {MARKER_VALUE}\n")
        ctx.git("commit", "-q", "-m", msg)
        log(f"committed regenerated .github ({len(tracked)} upstream file(s) removed)")

    # Report the series as it now stands, and anything dropped on the way.
    head = ctx.sha("HEAD")
    new_series = patch_series(ctx, plan["upstream_tag"], "HEAD")
    if plan.get("series"):
        old_subjects = [ctx.out("log", "-1", "--format=%s", c) for c in plan["series"]]
        new_subjects = [ctx.out("log", "-1", "--format=%s", c) for c in new_series]
        dropped = [s for s in old_subjects if s not in new_subjects]
        if dropped:
            log(f"WARNING: {len(dropped)} patch commit(s) were dropped as empty/absent: {dropped}")
        plan["dropped"] = dropped
    plan["head"] = head
    plan["new_series"] = new_series
    ctx.path("plan.json").write_text(json.dumps(plan, indent=2) + "\n")
    log(f"{branch} ready at {head[:10]} ({len(new_series)} patch commit(s) on {plan['upstream_tag']})")
    return EXIT_OK


def cmd_finalize(ctx: Ctx, a: argparse.Namespace) -> int:
    plan = load_plan(ctx)
    if plan["action"] not in ("replay", "release"):
        log(f"plan action is '{plan['action']}'; nothing to finalize")
        return EXIT_OK
    return finalize(ctx, plan)


# ------------------------------------------------------------------- gate


def export_tree(ctx: Ctx, sha: str, dest: Path) -> None:
    """Exactly the committed tree, independent of the working tree."""
    with tempfile.TemporaryFile() as tf:
        r = subprocess.run(["git", "-C", str(ctx.repo), "archive", "--format=tar", sha], stdout=tf)
        if r.returncode != 0:
            raise Fail(EXIT_STATE, f"git archive {sha} failed")
        tf.seek(0)
        with tarfile.open(fileobj=tf) as t:
            t.extractall(dest, filter="data")


CLASS_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|sealed|static|partial|abstract)\s+)*class\s+(\w+)", re.M)
NS_RE = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)


def fork_unit_tests(ctx: Ctx, base: str, sha: str, tree: Path) -> tuple[list[str], str]:
    files = ctx.out("diff", "--name-only", "--diff-filter=AM", f"{base}..{sha}", "--", "tests/").splitlines()
    files = [f for f in files if f.endswith(".cs")]
    projects: set[str] = set()
    filters: set[str] = set()
    for f in files:
        d = (tree / f).parent
        proj = None
        while d != tree and d != d.parent:
            cs = sorted(d.glob("*.csproj"))
            if cs:
                proj = cs[0]
                break
            d = d.parent
        if not proj:
            log(f"warning: {f} is not inside a test project; ignored")
            continue
        src = (tree / f).read_text(encoding="utf-8", errors="replace")
        ns = NS_RE.search(src)
        classes = CLASS_RE.findall(src)
        if not classes:
            continue
        projects.add(str(proj.relative_to(tree)))
        for c in classes:
            filters.add(f"FullyQualifiedName~{ns.group(1) + '.' if ns else ''}{c}")
    return sorted(projects), "|".join(sorted(filters))


def run_logged(cmd: list[str], logfile: Path, env: dict | None = None) -> int:
    log("$ " + " ".join(cmd))
    with open(logfile, "w") as lf:
        lf.write("$ " + " ".join(cmd) + "\n")
        p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                             env={**os.environ, **(env or {})})
        assert p.stdout
        for line in p.stdout:
            sys.stderr.write(line)
            lf.write(line)
        return p.wait()


def ref_plan(ctx: Ctx, ref: str) -> dict:
    """A throwaway plan for gating an arbitrary commit (CI on pushes/PRs)."""
    sha = ctx.sha(ref)
    tag = ctx.out("describe", "--tags", "--abbrev=0", "--match", "v[0-9]*", "--exclude", "*-*", sha)
    if not STABLE_TAG_RE.match(tag):
        raise Fail(EXIT_STATE, f"{ref}: nearest tag {tag!r} is not an upstream release tag")
    v = tag[1:]
    return {"action": "release", "version": v, "upstream_tag": tag, "branch": None, "ref": sha,
            "fork_version": f"{v}-oidc.ci", "fork_tag": None}


def cmd_gate(ctx: Ctx, a: argparse.Namespace) -> int:
    if a.ref:
        plan = ref_plan(ctx, a.ref)
        sha = plan["ref"]
        branch = a.ref
    else:
        plan = load_plan(ctx)
        if plan["action"] not in ("replay", "release"):
            log(f"plan action is '{plan['action']}'; nothing to gate")
            return EXIT_OK
        branch = plan["branch"]
        sha = ctx.sha(f"refs/heads/{branch}")
    if sequencer_in_progress(ctx):
        raise Fail(EXIT_CONFLICT, "cherry-pick in progress: refusing to gate a conflicted state")
    tracked = [f for f in ctx.out("ls-tree", "-r", "--name-only", sha, "--", ".github").splitlines() if f]
    if tracked != [".github/workflows/ci.yml"]:
        raise Fail(EXIT_STATE, f"{branch} still carries upstream .github ({len(tracked)} files); run `finalize`")
    image = a.image or f"jellyfin-oidc:{plan['fork_version']}"
    results = Path(a.results or ctx.path("e2e-results"))
    results.mkdir(parents=True, exist_ok=True)
    gate_file = ctx.state_dir / "gate.json"
    if gate_file.exists():
        gate_file.unlink()

    unit_ran = False
    build_dir = Path(tempfile.mkdtemp(prefix="jellyfin-oidc-src-", dir=a.tmpdir))
    try:
        export_tree(ctx, sha, build_dir)
        common = ["--build-arg", f"UPSTREAM_VERSION={plan['version']}",
                  "--build-arg", f"FORK_VERSION={plan['fork_version']}",
                  "--build-arg", f"SOURCE_REVISION={sha}",
                  "--build-arg", f"SOURCE_REPO=https://github.com/{ctx.github_repo}",
                  "-f", str(AUTOMATION_DIR / "Dockerfile")]
        if not a.skip_build:
            rc = run_logged(["docker", "buildx", "build", "--progress=plain", *common,
                             "--platform", "linux/amd64", "-t", image, "--load", str(build_dir)],
                            ctx.path("build.log"))
            if rc != 0:
                raise Fail(EXIT_BUILD, f"image build failed (exit {rc}); log: {ctx.path('build.log')}")
        if not a.skip_unit:
            projects, filt = fork_unit_tests(ctx, plan["upstream_tag"], sha, build_dir)
            if not projects:
                if a.allow_no_unit_tests:
                    log("WARNING: no fork unit tests found under tests/ (allowed by --allow-no-unit-tests)")
                else:
                    raise Fail(EXIT_UNIT, "no fork unit tests found under tests/ in the patch series")
            else:
                log(f"unit tests: {projects} filter={filt}")
                rc = run_logged(["docker", "buildx", "build", "--progress=plain", *common, "--target", "test",
                                 "--build-arg", f"TEST_PROJECTS={' '.join(projects)}",
                                 "--build-arg", f"TEST_FILTER={filt}",
                                 "--output", "type=cacheonly", str(build_dir)], ctx.path("unit.log"))
                if rc != 0:
                    raise Fail(EXIT_UNIT, f"unit tests failed (exit {rc}); log: {ctx.path('unit.log')}")
                unit_ran = True
    finally:
        shutil.rmtree(build_dir, ignore_errors=True)

    image_id = subprocess.run(["docker", "image", "inspect", "-f", "{{.Id}}", image],
                              capture_output=True, text=True)
    if image_id.returncode != 0:
        raise Fail(EXIT_BUILD, f"image {image} not present after build")
    if not a.skip_e2e:
        rc = run_logged([str(AUTOMATION_DIR / "e2e" / "run.sh"), "--results", str(results), image],
                        ctx.path("e2e.log"), env={"E2E_TMPDIR": a.tmpdir or tempfile.gettempdir()})
        if rc == 1:
            raise Fail(EXIT_E2E, f"e2e gate failed; results in {results}")
        if rc != 0:
            raise Fail(EXIT_INFRA, f"e2e harness error (exit {rc}); results in {results}")
    skipped = [s for s in ("build", "unit", "e2e") if getattr(a, f"skip_{s}")]
    if not unit_ran and "unit" not in skipped:
        skipped.append("unit (no fork tests found)")
    if a.ref:
        skipped.append("ad-hoc --ref gate")
    gate = {"sha": sha, "branch": branch, "image": image, "image_id": image_id.stdout.strip(),
            "fork_version": plan["fork_version"], "passed": not skipped, "skipped": skipped}
    gate_file.write_text(json.dumps(gate, indent=2) + "\n")
    if gate["passed"]:
        log(f"GATE PASSED for {branch} @ {sha[:10]} -> {image}")
    else:
        log(f"gate finished but skipped {skipped}: the result is NOT publishable")
    return EXIT_OK


# ---------------------------------------------------------------- publish


def run(cmd: list[str], what: str, dry: bool) -> None:
    log(("(dry-run) " if dry else "") + "$ " + " ".join(cmd))
    if dry:
        return
    r = subprocess.run(cmd)
    if r.returncode != 0:
        raise Fail(EXIT_PUBLISH, f"{what} failed (exit {r.returncode})")


def release_notes(ctx: Ctx, plan: dict, sha: str) -> str:
    v, tag = plan["version"], plan["fork_tag"]
    commits = ctx.out("log", "--reverse", "--no-merges", "--format=- %h %s",
                      f"--grep=^{MARKER_TRAILER}: {MARKER_VALUE}$", "--invert-grep",
                      f"{plan['upstream_tag']}..{sha}")
    img = plan["image_repo"]
    dropped = plan.get("dropped") or []
    lines = [
        f"Unofficial Jellyfin Server {v} with built-in OpenID Connect login. "
        "Not affiliated with the Jellyfin project.",
        "",
        f"- Upstream release: https://github.com/jellyfin/jellyfin/releases/tag/v{v}",
        f"- Source for this image: https://github.com/{ctx.github_repo}/tree/{tag} (branch `{plan['branch']}`)",
        f"- Image: `{img}:{plan['fork_version']}` (also `{img}:{v}`" + (", `latest`)" if plan.get("latest") else ")"),
        "",
        f"## Patch commits on top of v{v}",
        "",
        commits or "(none)",
    ]
    if plan.get("prev_branch"):
        lines += ["", f"Replayed automatically from `{plan['prev_branch']}`."]
    if dropped:
        lines += ["", "Dropped as already contained upstream: " + "; ".join(dropped)]
    lines += ["", "Published only after the build, the fork's unit tests and the end-to-end OIDC test passed."]
    return "\n".join(lines) + "\n"


def cmd_publish(ctx: Ctx, a: argparse.Namespace) -> int:
    plan = load_plan(ctx)
    if plan["action"] not in ("replay", "release"):
        log(f"plan action is '{plan['action']}'; nothing to publish")
        return EXIT_OK
    what = set(a.what.split(","))
    bad = what - {"git", "image", "release"}
    if bad:
        raise Fail(EXIT_USAGE, f"unknown --what parts: {bad}")
    gate_file = ctx.state_dir / "gate.json"
    if not gate_file.exists():
        raise Fail(EXIT_STATE, "no gate result; refusing to publish an untested state")
    gate = json.loads(gate_file.read_text())
    branch = plan["branch"]
    sha = ctx.sha(f"refs/heads/{branch}")
    if not gate.get("passed"):
        if not ctx.dry_run:
            raise Fail(EXIT_STATE, f"gate did not fully pass (skipped: {gate.get('skipped')}); refusing to publish")
        log(f"WARNING: gate skipped {gate.get('skipped')}; a real publish would refuse. Dry-run continues.")
    if gate["sha"] != sha:
        raise Fail(EXIT_STATE, f"{branch} is at {sha[:10]} but the gate ran on {gate['sha'][:10]}; re-run the gate")
    if sequencer_in_progress(ctx):
        raise Fail(EXIT_CONFLICT, "cherry-pick in progress")
    tag, fv, v = plan["fork_tag"], plan["fork_version"], plan["version"]
    dry = ctx.dry_run

    if "git" in what:
        # Source first: no image is ever published without its source tag.
        if ctx.ref_exists(f"refs/tags/{tag}"):
            if ctx.sha(f"refs/tags/{tag}") != sha:
                raise Fail(EXIT_STATE, f"tag {tag} exists locally and points elsewhere")
        else:
            cmd = ["git", "-C", str(ctx.repo), *GIT_NOSIGN, "tag", "-a", "-m",
                   f"jellyfin-oidc {fv} (upstream v{v})", tag, sha]
            run(cmd, "git tag", dry)
        # Upstream's own tags are deliberately NOT pushed: a pushed tag runs the
        # workflows of the tagged commit, and upstream's .github has a
        # `push: tags: v*` workflow. release.py always fetches them from upstream.
        run(["git", "-C", str(ctx.repo), "push", ctx.origin,
             f"refs/heads/{branch}:refs/heads/{branch}"], "git push branch", dry)
        run(["git", "-C", str(ctx.repo), "push", ctx.origin, f"refs/tags/{tag}:refs/tags/{tag}"], "git push tag", dry)

    if "image" in what:
        img = plan["image_repo"]
        local = gate["image"]
        got = subprocess.run(["docker", "image", "inspect", "-f", "{{.Id}}", local], capture_output=True, text=True)
        if got.returncode != 0 or got.stdout.strip() != gate["image_id"]:
            raise Fail(EXIT_STATE, f"local image {local} is not the image the gate tested ({gate['image_id'][:19]})")
        # amd64: exactly the bytes the e2e ran against.
        run(["docker", "tag", local, f"{img}:{fv}-amd64"], "docker tag", dry)
        run(["docker", "push", f"{img}:{fv}-amd64"], "docker push amd64", dry)
        parts = [f"{img}:{fv}-amd64"]
        if not a.no_arm64:
            # arm64: same commit, cross-published (not run-tested; no arm64 e2e).
            with tempfile.TemporaryDirectory(prefix="jellyfin-oidc-src-", dir=a.tmpdir) as d:
                export_tree(ctx, sha, Path(d))
                run(["docker", "buildx", "build", "--platform", "linux/arm64",
                     "-f", str(AUTOMATION_DIR / "Dockerfile"),
                     "--build-arg", f"UPSTREAM_VERSION={v}", "--build-arg", f"FORK_VERSION={fv}",
                     "--build-arg", f"SOURCE_REVISION={sha}",
                     "--build-arg", f"SOURCE_REPO=https://github.com/{ctx.github_repo}",
                     "--provenance=false", "-t", f"{img}:{fv}-arm64", "--push", d], "arm64 build/push", dry)
            parts.append(f"{img}:{fv}-arm64")
        tags = [f"{img}:{fv}", f"{img}:{v}"] + ([f"{img}:latest"] if plan.get("latest") else [])
        tag_args = [x for t in tags for x in ("-t", t)]
        run(["docker", "buildx", "imagetools", "create", *tag_args, *parts], "multi-arch manifest", dry)

    if "release" in what:
        notes = release_notes(ctx, plan, sha)
        nf = ctx.path("release-notes.md")
        nf.write_text(notes)
        exists = False if dry else subprocess.run(
            ["gh", "release", "view", tag, "-R", ctx.github_repo], capture_output=True).returncode == 0
        if exists:
            log(f"GitHub release {tag} already exists")
        else:
            cmd = ["gh", "release", "create", tag, "-R", ctx.github_repo, "--verify-tag",
                   "--title", f"{tag} (Jellyfin {v} + OIDC)", "--notes-file", str(nf)]
            if not plan.get("latest"):
                cmd.append("--latest=false")
            run(cmd, "gh release create", dry)
        if dry:
            log(f"release notes would be:\n{notes}")
    log(f"published {tag}" if not dry else f"dry-run publish of {tag} complete")
    return EXIT_OK


# -------------------------------------------------------------------- run


def cmd_run(ctx: Ctx, a: argparse.Namespace) -> int:
    rc = cmd_plan(ctx, a)
    plan = load_plan(ctx)
    if plan["action"] in ("noop", "wait"):
        return rc
    if plan["action"] == "replay":
        rc = cmd_replay(ctx, a)
        if rc != EXIT_OK:
            return rc
    else:
        rc = finalize(ctx, plan)
        if rc != EXIT_OK:
            return rc
    rc = cmd_gate(ctx, a)
    if rc != EXIT_OK:
        return rc
    if a.no_publish:
        log("--no-publish: stopping after the gate; showing what publish would do")
        ctx.dry_run = True
    return cmd_publish(ctx, a)


# ------------------------------------------------------------------- main


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--repo", default=os.environ.get("JFO_REPO", "."), help="fork checkout (default: cwd)")
    p.add_argument("--origin", default="origin", help="remote of the fork repo")
    p.add_argument("--upstream", default="upstream", help="remote of jellyfin/jellyfin (added if missing)")
    p.add_argument("--upstream-url", default="https://github.com/jellyfin/jellyfin.git")
    p.add_argument("--state-dir", help="default: <git-dir>/jellyfin-oidc-release")
    p.add_argument("--image-repo", default=os.environ.get("JFO_IMAGE_REPO", "ghcr.io/igor47/jellyfin-oidc"))
    p.add_argument("--github-repo", default=os.environ.get("JFO_GITHUB_REPO", "igor47/jellyfin-oidc"))
    p.add_argument("--tmpdir", default=os.environ.get("JFO_TMPDIR"), help="for exported source trees and e2e")
    sub = p.add_subparsers(dest="cmd", required=True)

    def plan_args(sp: argparse.ArgumentParser) -> None:
        g = sp.add_mutually_exclusive_group()
        g.add_argument("--version", help="upstream version (e.g. 12.4); default: latest stable")
        g.add_argument("--branch", help="re-release an existing patch branch (e.g. oidc/v12.4)")
        sp.add_argument("--no-fetch", action="store_true", help="use refs already in the repo")
        sp.add_argument("--fresh", action="store_true", help="redo a replay whose branch exists only locally")
        sp.add_argument("--skip-image-check", action="store_true", help="don't require jellyfin/jellyfin:<v> on Docker Hub")
        sp.add_argument("--skip-release-check", action="store_true", help="tag on origin == released (no GitHub API)")

    def gate_args(sp: argparse.ArgumentParser) -> None:
        sp.add_argument("--ref", help="gate this commit instead of the planned branch (no plan needed; never publishable)")
        sp.add_argument("--image", help="local tag for the built image (default jellyfin-oidc:<fork version>)")
        sp.add_argument("--results", help="e2e results dir (default <state-dir>/e2e-results)")
        sp.add_argument("--skip-build", action="store_true", help="(debug) reuse --image; result is not publishable")
        sp.add_argument("--skip-unit", action="store_true", help="(debug) result is not publishable")
        sp.add_argument("--skip-e2e", action="store_true", help="(debug) result is not publishable")
        sp.add_argument("--allow-no-unit-tests", action="store_true",
                        help="(debug) don't fail when the series has no tests; result is not publishable")

    def pub_args(sp: argparse.ArgumentParser) -> None:
        sp.add_argument("--what", default="git,image,release", help="comma list of git,image,release")
        sp.add_argument("--dry-run", action="store_true", help="print the commands instead of running them")
        sp.add_argument("--no-arm64", action="store_true", help="publish linux/amd64 only")

    plan_args(sub.add_parser("plan", help="decide what to do"))
    rp = sub.add_parser("replay", help="create the new branch and cherry-pick the series")
    rp.add_argument("--fresh", action="store_true", help="delete an existing local branch first")
    sub.add_parser("continue", help="continue a replay after resolving a conflict")
    sub.add_parser("finalize", help="regenerate the .github commit on the planned branch")
    gate_args(sub.add_parser("gate", help="build + unit tests + e2e"))
    pub_args(sub.add_parser("publish", help="push branch/tag/images, create the release"))
    r = sub.add_parser("run", help="plan + replay + gate + publish")
    plan_args(r)
    gate_args(r)
    pub_args(r)
    r.add_argument("--no-publish", action="store_true", help="stop after the gate (prints the publish commands)")

    a = p.parse_args(argv)
    repo = Path(a.repo).resolve()
    try:
        gd = subprocess.run(["git", "-C", str(repo), "rev-parse", "--absolute-git-dir"],
                            capture_output=True, text=True)
        if gd.returncode != 0:
            raise Fail(EXIT_USAGE, f"{repo} is not a git repository")
        ctx = Ctx(repo=repo, origin=a.origin, upstream=a.upstream, upstream_url=a.upstream_url,
                  state_dir=Path(a.state_dir) if a.state_dir else Path(gd.stdout.strip()) / "jellyfin-oidc-release",
                  image_repo=a.image_repo, github_repo=a.github_repo,
                  dry_run=getattr(a, "dry_run", False))
        handler = {"plan": cmd_plan, "replay": cmd_replay, "continue": cmd_continue, "finalize": cmd_finalize,
                   "gate": cmd_gate, "publish": cmd_publish, "run": cmd_run}[a.cmd]
        return handler(ctx, a)
    except Fail as e:
        log(f"ERROR ({e.code}): {e}")
        return e.code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
