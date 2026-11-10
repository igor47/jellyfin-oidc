#!/usr/bin/env bash
# `cond && pass || fail` below is deliberate: pass only prints.
# shellcheck disable=SC2015
# Exercise scripts/release.py's replay logic for real, in a scratch directory,
# without touching any real checkout or remote.
#
#   scripts/test-replay.sh <jellyfin-clone> <scratch-dir> [--with-build]
#
# <jellyfin-clone> is any local clone of jellyfin/jellyfin with tags v12.3 and
# v12.4; it is only read (cloned with --shared). The scratch dir gets:
#   upstream.git  bare --shared clone of it, plus a synthetic upstream tag v12.5
#   fork.git      bare repo standing in for igor47/jellyfin-oidc
#   work          the checkout release.py operates on
#
# Scenarios:
#   1. synthetic oidc/v12.3: two patch commits (UserController.cs hook + a new
#      file, then a unit test); release.py finalizes it (drops .github)
#      [--with-build: gate it: image build + the synthetic unit test]
#   2. replay oidc/v12.3 -> v12.4: clean; .github handled by regeneration
#   3. idempotency: once published, plan says noop; a new commit -> re-release N=2
#   4. conflict: synthetic upstream v12.5 edits the same UserController.cs line
#      AND upstream's .github; replay stops with exit 3 and conflict.json
#      naming the commit and file; .github causes no conflict; resolving and
#      `continue` completes the series
set -euo pipefail

SRC=${1:?usage: test-replay.sh <jellyfin-clone> <scratch-dir> [--with-build]}
SCRATCH=${2:?usage}
WITH_BUILD=${3:-}
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REL="python3 $HERE/release.py"

if [[ -e "$SCRATCH/work" || -e "$SCRATCH/fork.git" || -e "$SCRATCH/upstream.git" ]]; then
  echo "scratch dir $SCRATCH already has a fixture; use an empty directory" >&2
  exit 2
fi
mkdir -p "$SCRATCH"
SCRATCH=$(cd "$SCRATCH" && pwd)
STATE="$SCRATCH/state"
pass() { printf 'PASS  %s\n' "$*"; }
fail() { printf 'FAIL  %s\n' "$*"; exit 1; }
nosign() { git -C "$1" config commit.gpgsign false; git -C "$1" config tag.gpgsign false; }

git clone -q --bare --shared "$SRC" "$SCRATCH/upstream.git"
nosign "$SCRATCH/upstream.git"
git init -q --bare "$SCRATCH/fork.git"
git clone -q --shared "$SRC" "$SCRATCH/work"
W="$SCRATCH/work"
nosign "$W"
git -C "$W" config user.name "replay test"
git -C "$W" config user.email "replay-test@example.invalid"
git -C "$W" remote rename origin upstream
git -C "$W" remote set-url upstream "$SCRATCH/upstream.git"
git -C "$W" remote add origin "$SCRATCH/fork.git"
# Start with no local branches (the clone checked out the source's HEAD branch).
git -C "$W" checkout -q --detach v12.3
git -C "$W" for-each-ref --format='%(refname:short)' refs/heads/ | xargs -r git -C "$W" branch -q -D
# The real fork contains upstream history and tags.
git -C "$W" push -q origin refs/tags/v12.3 refs/tags/v12.4

R() { $REL --repo "$W" --state-dir "$STATE" --github-repo test/jellyfin-oidc --image-repo localhost/jellyfin-oidc-test "$@"; }

# ---------------------------------------------------------------- scenario 1
git -C "$W" checkout -q -b oidc/v12.3 v12.3
UC=Jellyfin.Api/Controllers/UserController.cs
python3 - "$W/$UC" <<'EOF'
import sys, re
p = sys.argv[1]
s = open(p).read()
needle = "    public async Task<ActionResult<AuthenticationResult>> AuthenticateUserByName([FromBody, Required] AuthenticateUserByName request)\n    {\n"
assert needle in s, "hook point not found"
s = s.replace(needle, needle + "        // jellyfin-oidc synthetic hook: password login gate goes here\n", 1)
open(p, "w").write(s)
EOF
mkdir -p "$W/Jellyfin.Api/Oidc"
cat > "$W/Jellyfin.Api/Oidc/SyntheticHook.cs" <<'EOF'
namespace Jellyfin.Api.Oidc;

/// <summary>
/// Synthetic fork file for the replay test.
/// </summary>
public static class SyntheticHook
{
    /// <summary>
    /// Adds one.
    /// </summary>
    /// <param name="x">A number.</param>
    /// <returns>x + 1.</returns>
    public static int AddOne(int x) => x + 1;
}
EOF
git -C "$W" add -A && git -C "$W" commit -q -m "synthetic: hook in UserController + new file"
mkdir -p "$W/tests/Jellyfin.Api.Tests/Oidc"
cat > "$W/tests/Jellyfin.Api.Tests/Oidc/SyntheticHookTests.cs" <<'EOF'
using Jellyfin.Api.Oidc;
using Xunit;

namespace Jellyfin.Api.Tests.Oidc;

public class SyntheticHookTests
{
    [Fact]
    public void AddOne_AddsOne()
    {
        Assert.Equal(2, SyntheticHook.AddOne(1));
    }
}
EOF
git -C "$W" add -A && git -C "$W" commit -q -m "synthetic: unit test for the hook"
git -C "$W" push -q origin oidc/v12.3

R plan --branch oidc/v12.3 --skip-image-check --skip-release-check >/dev/null
[[ $(jq -r .action "$STATE/plan.json") == release && $(jq -r .n "$STATE/plan.json") == 1 ]] \
  && pass "plan: unreleased existing branch -> release N=1" || fail "plan for oidc/v12.3: $(cat "$STATE/plan.json")"
R finalize
[[ $(git -C "$W" ls-tree -r --name-only oidc/v12.3 -- .github) == ".github/workflows/ci.yml" ]] \
  && pass "finalize: upstream .github replaced by the CI stub in one commit" || fail "finalize"
if [[ "$WITH_BUILD" == --with-build ]]; then
  rc=0; R gate --skip-e2e --image jellyfin-oidc:replay-test || rc=$?
  [[ $rc == 0 ]] && pass "gate (build + synthetic unit test) on oidc/v12.3" || fail "gate rc=$rc"
  # A cached test step means these exact inputs passed before (failures are never cached).
  grep -q "TEST_FILTER=FullyQualifiedName~Jellyfin.Api.Tests.Oidc.SyntheticHookTests" "$STATE/unit.log" \
    && grep -qE 'Test Run Successful|CACHED' "$STATE/unit.log" \
    && pass "unit test discovery selected and ran SyntheticHookTests" || fail "unit tests did not run the synthetic test"
  [[ $(jq -r .passed "$STATE/gate.json") == false ]] && pass "gate with --skip-e2e is marked not publishable" || fail "skip-e2e gate marked publishable"
  docker image rm -f jellyfin-oidc:replay-test >/dev/null
fi
# Publish it by hand, as a human would have for the first release.
git -C "$W" tag -a -m "test" v12.3-oidc.1 oidc/v12.3
git -C "$W" push -q origin oidc/v12.3 v12.3-oidc.1

# ---------------------------------------------------------------- scenario 2
R plan --version 12.4 --skip-image-check --skip-release-check >/dev/null
[[ $(jq -r .action "$STATE/plan.json") == replay && $(jq -r '.series|length' "$STATE/plan.json") == 2 ]] \
  && pass "plan v12.4: replay 2 commits from oidc/v12.3 (stub commit excluded)" || fail "plan v12.4: $(cat "$STATE/plan.json")"
R replay
n=$(git -C "$W" rev-list --count v12.4..oidc/v12.4)
[[ $n == 3 ]] && pass "replay v12.4: 2 patch commits + regenerated .github commit" || fail "replay produced $n commits"
git -C "$W" diff --quiet oidc/v12.3 oidc/v12.4 -- "$UC" Jellyfin.Api/Oidc tests/Jellyfin.Api.Tests/Oidc \
  && pass "replayed patch content identical to oidc/v12.3" || fail "content differs"
[[ $(git -C "$W" log -1 --format=%an oidc/v12.4~1) == "replay test" ]] && pass "authorship preserved" || fail "author"
[[ $(git -C "$W" ls-tree -r --name-only oidc/v12.4 -- .github) == ".github/workflows/ci.yml" ]] \
  && pass "oidc/v12.4 carries no upstream .github" || fail ".github on oidc/v12.4"

# ---------------------------------------------------------------- scenario 3
git -C "$W" tag -a -m test v12.4-oidc.1 oidc/v12.4
git -C "$W" push -q origin oidc/v12.4 v12.4-oidc.1
R plan --version 12.4 --skip-image-check --skip-release-check >/dev/null
[[ $(jq -r .action "$STATE/plan.json") == noop ]] && pass "idempotent: v12.4 already released -> noop" || fail "noop: $(cat "$STATE/plan.json")"
git -C "$W" checkout -q oidc/v12.4
echo "// fork-only fix" >> "$W/Jellyfin.Api/Oidc/SyntheticHook.cs"
git -C "$W" commit -q -am "synthetic: fork-only fix"
git -C "$W" push -q origin oidc/v12.4
R plan --branch oidc/v12.4 --skip-image-check --skip-release-check >/dev/null
[[ $(jq -r .action "$STATE/plan.json") == release && $(jq -r .fork_tag "$STATE/plan.json") == v12.4-oidc.2 ]] \
  && pass "re-release: new commit on oidc/v12.4 -> v12.4-oidc.2" || fail "rerelease: $(cat "$STATE/plan.json")"
R finalize >/dev/null
[[ $(git -C "$W" rev-list --count v12.4..oidc/v12.4) == 4 ]] && pass "finalize leaves a correct stub alone (no extra commit)" || fail "finalize added a commit"
git -C "$W" tag -a -m test v12.4-oidc.2 oidc/v12.4
git -C "$W" push -q origin oidc/v12.4 v12.4-oidc.2

# ---------------------------------------------------------------- scenario 4
# Synthetic upstream release v12.5: changes the line after our hook point and
# rewrites an upstream workflow (which our branches deleted).
U=$(mktemp -d "$SCRATCH/u.XXXX")
git clone -q --shared "$SCRATCH/upstream.git" "$U"
nosign "$U"
git -C "$U" config user.name "upstream" && git -C "$U" config user.email "upstream@example.invalid"
git -C "$U" checkout -q -b rel v12.4
python3 - "$U/$UC" <<'EOF'
import sys
p = sys.argv[1]
s = open(p).read()
needle = "    public async Task<ActionResult<AuthenticationResult>> AuthenticateUserByName([FromBody, Required] AuthenticateUserByName request)\n    {\n"
assert needle in s
s = s.replace(needle, needle + "        ArgumentNullException.ThrowIfNull(request);\n", 1)
open(p, "w").write(s)
EOF
wf=$(git -C "$U" ls-files .github/workflows | head -1)
echo "# upstream change in v12.5" >> "$U/$wf"
git -C "$U" commit -q -am "upstream: v12.5 changes"
git -C "$U" tag -a -m "v12.5" v12.5
git -C "$U" push -q origin v12.5
rm -rf "$U"

R plan --version 12.5 --skip-image-check --skip-release-check >/dev/null
[[ $(jq -r .prev_branch "$STATE/plan.json") == oidc/v12.4 && $(jq -r '.series|length' "$STATE/plan.json") == 3 ]] \
  && pass "plan v12.5: replay 3 commits from oidc/v12.4" || fail "plan v12.5: $(cat "$STATE/plan.json")"
rc=0; R replay || rc=$?
[[ $rc == 3 ]] && pass "conflict: replay exits 3" || fail "replay exit $rc (expected 3)"
jq . "$STATE/conflict.json"
[[ $(jq -r '.conflicted_files|join(",")' "$STATE/conflict.json") == "$UC" ]] \
  && pass "conflict.json names $UC (and not .github)" || fail "conflicted files"
[[ $(jq -r .commit_subject "$STATE/conflict.json") == "synthetic: hook in UserController + new file" ]] \
  && pass "conflict.json names the conflicting commit" || fail "conflict commit"
rc=0; R gate --skip-e2e >/dev/null 2>&1 || rc=$?
[[ $rc == 3 ]] && pass "gate refuses a conflicted state (exit 3)" || fail "gate on conflict exit $rc"
rc=0; R continue >/dev/null 2>&1 || rc=$?
[[ $rc == 3 ]] && pass "continue with unresolved files exits 3 again" || fail "continue exit $rc"

# Resolve: keep both lines (upstream's null check, then our hook).
python3 - "$W/$UC" <<'EOF'
import sys, re
p = sys.argv[1]
s = open(p).read()
s = re.sub(r"<<<<<<< [^\n]*\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n", lambda m: m.group(1) + m.group(2), s, flags=re.S)
s = s.replace("        ArgumentNullException.ThrowIfNull(request);\n        ArgumentNullException.ThrowIfNull(request);\n",
              "        ArgumentNullException.ThrowIfNull(request);\n")
open(p, "w").write(s)
EOF
git -C "$W" add "$UC"
R continue
[[ ! -e "$STATE/conflict.json" ]] && pass "continue completes the series and clears conflict.json" || fail "conflict.json left"
[[ $(git -C "$W" rev-list --count v12.5..oidc/v12.5) == 4 ]] && pass "oidc/v12.5: 3 patch commits + regenerated .github" || fail "count"
[[ $(git -C "$W" ls-tree -r --name-only oidc/v12.5 -- .github) == ".github/workflows/ci.yml" ]] \
  && pass "upstream's .github change caused no conflict and is dropped" || fail ".github on v12.5"
grep -q "jellyfin-oidc synthetic hook" <(git -C "$W" show "oidc/v12.5:$UC") && \
  grep -q "ThrowIfNull(request)" <(git -C "$W" show "oidc/v12.5:$UC") && pass "resolution kept both changes" || fail "resolution"

echo "all replay scenarios passed"
