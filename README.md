# jellyfin-oidc

Jellyfin Server with OpenID Connect login built in, and a switch to turn
password login off.

This is an **unofficial** build. It is not affiliated with, endorsed by or
supported by the Jellyfin project. Please do not report problems with it to
Jellyfin; open an issue here instead.

- Image: `ghcr.io/igor47/jellyfin-oidc`
- License: GPL-2.0, like Jellyfin. The complete source of every published
  image is the git tag of the same name in this repository: image
  `X.Y-oidc.N` is built from tag `vX.Y-oidc.N` (branch `oidc/vX.Y`), which is
  upstream's `vX.Y` plus a small patch series. The image's OCI labels name the
  exact commit (`org.opencontainers.image.revision`).
- The patch is server-only. The web client is upstream's, unmodified.
- No database schema changes. You can switch between this image and stock
  Jellyfin of the same version (see [Moving back to stock
  Jellyfin](#moving-back-to-stock-jellyfin)).

The design contract, including what the patch must and must not do, is in
[DESIGN.md](DESIGN.md).

## What is in this branch

This `main` branch holds only the build and release automation. The server
source lives on the `oidc/vX.Y` branches.

| Path | What |
|---|---|
| `Dockerfile` | Builds the server from a patch branch and lays it over the official `jellyfin/jellyfin:X.Y` image. |
| `e2e/` | End-to-end test: throwaway Keycloak + the image + headless Chromium driving the stock web client. |
| `scripts/release.py` | Detects upstream releases, replays the patch series, gates, publishes. |
| `scripts/test-replay.sh` | Exercises the replay logic (clean replay, re-release, conflict) in a scratch directory. |
| `.github/workflows/` | `upstream-release.yml` (scheduled release pipeline), `build.yml` (CI for patch branches). |

## Quick start

```yaml
services:
  jellyfin:
    image: ghcr.io/igor47/jellyfin-oidc:12.4   # or 12.4-oidc.1 to pin exactly
    restart: unless-stopped
    # The image has a built-in healthcheck (/health). With invalid OIDC
    # settings the server fails at startup and the container shows as
    # unhealthy (and later exits); watch `docker ps` after changing settings.
    ports:
      - "8096:8096"
    volumes:
      - ./config:/config
      - ./cache:/cache
      - /srv/media:/media:ro
      - ./oidc-client-secret:/run/secrets/oidc-client-secret:ro
    environment:
      JELLYFIN_PublishedServerUrl: https://jellyfin.example.org
      JELLYFIN_OIDC_ENABLED: "true"
      JELLYFIN_OIDC_ISSUER: https://auth.example.org/application/o/jellyfin/
      JELLYFIN_OIDC_CLIENT_ID: jellyfin
      JELLYFIN_OIDC_CLIENT_SECRET_FILE: /run/secrets/oidc-client-secret
      JELLYFIN_OIDC_PROVIDER_NAME: Authentik
      # Required with AUTO_CREATE_USERS (the default), unless you set
      # JELLYFIN_OIDC_ALLOW_ANY_USER: "true" to let every IdP user in.
      JELLYFIN_OIDC_ALLOWED_GROUPS: jellyfin-users,jellyfin-admins
      JELLYFIN_OIDC_ADMIN_GROUPS: jellyfin-admins
      # Required (or JELLYFIN_PublishedServerUrl); never taken from the request.
      JELLYFIN_OIDC_REDIRECT_BASE_URL: https://jellyfin.example.org
      # Turn this on once OIDC login works for you (see "Break-glass"):
      # JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN: "true"
```

Everything else about the image (paths, ffmpeg, hardware acceleration, the
`/config` and `/cache` volumes, the healthcheck) is exactly as in the official
`jellyfin/jellyfin` image; its documentation applies.

Prefer `JELLYFIN_OIDC_CLIENT_SECRET_FILE` over `JELLYFIN_OIDC_CLIENT_SECRET`:
it keeps the secret out of `docker inspect`, the process environment and
compose files. (Upstream Jellyfin also writes every `JELLYFIN_*` variable
into its log at startup.)

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `JELLYFIN_OIDC_ENABLED` | `false` | Master switch. |
| `JELLYFIN_OIDC_ISSUER` | | Issuer URL; discovery at `<issuer>/.well-known/openid-configuration`. |
| `JELLYFIN_OIDC_CLIENT_ID` | | |
| `JELLYFIN_OIDC_CLIENT_SECRET` | | Confidential client secret. This or `_FILE` is required; public clients are not supported. |
| `JELLYFIN_OIDC_CLIENT_SECRET_FILE` | | Alternative: path to a file holding the secret. |
| `JELLYFIN_OIDC_SCOPES` | `openid profile email` | Space separated; must contain `openid`. |
| `JELLYFIN_OIDC_USERNAME_CLAIM` | `preferred_username` | Claim used as the Jellyfin username for new or linked users. |
| `JELLYFIN_OIDC_GROUPS_CLAIM` | `groups` | Claim holding group names (array or string). |
| `JELLYFIN_OIDC_ALLOWED_GROUPS` | (empty) | Comma separated. Login denied unless the user is in one. Empty with `AUTO_CREATE_USERS=true` is a startup error unless `ALLOW_ANY_USER=true`. |
| `JELLYFIN_OIDC_ALLOW_ANY_USER` | `false` | Explicitly allow every user of the identity provider to log in (and be created) when `ALLOWED_GROUPS` is empty. |
| `JELLYFIN_OIDC_ADMIN_GROUPS` | (empty = don't manage) | Comma separated. When set, administrator status is set on every login to match membership. |
| `JELLYFIN_OIDC_AUTO_CREATE_USERS` | `true` | Create a Jellyfin user on first login. |
| `JELLYFIN_OIDC_LINK_EXISTING_BY_USERNAME` | `false` | Migration aid: on first login, adopt an existing Jellyfin user with the same username (case-insensitive). See [Security notes](#security-notes). |
| `JELLYFIN_OIDC_PROVIDER_NAME` | `SSO` | Button label: "Sign in with <name>". |
| `JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN` | `false` | Reject all username/password authentication. |
| `JELLYFIN_OIDC_REDIRECT_BASE_URL` | (published server URL) | External base URL, e.g. `https://jellyfin.example.org`. If unset, Jellyfin's `JELLYFIN_PublishedServerUrl` is used; one of the two is required. It is never derived from the request's Host header. |

With `JELLYFIN_OIDC_ENABLED=true`, invalid or incomplete settings (no issuer,
client ID or secret, `SCOPES` without `openid`, no base URL, or no allowed
groups without `ALLOW_ANY_USER`) are a startup error, not a silent fallback to
password login. Upstream Jellyfin keeps the failed process alive for a while
before it exits; the container healthcheck is what shows it.

Users must reach Jellyfin at exactly the base URL, since the login state
cookie is set for it (with the `__Host-` prefix over HTTPS).

The redirect URI to register at your identity provider is
`<base>/oidc/callback`, e.g. `https://jellyfin.example.org/oidc/callback`.

## Authentik setup

1. **Applications → Providers → Create → OAuth2/OpenID Provider.**
   - Client type: **Confidential**.
   - Redirect URIs: `https://jellyfin.example.org/oidc/callback` (strict).
   - Signing key: pick a certificate (for example the self-signed
     `authentik Self-signed Certificate`). Without one, Authentik signs ID
     tokens with HS256 and the client secret; the fork validates signatures
     against the provider's JWKS and expects an asymmetric key.
   - Scopes: the defaults `openid`, `email`, `profile`. Authentik's default
     `profile` scope mapping already includes a `groups` claim with the user's
     group names, which is what `JELLYFIN_OIDC_GROUPS_CLAIM=groups` reads.
   - Subject mode: any; the fork keys users on (`iss`, `sub`). Do not change it
     after users have logged in, or they become new users.
   - Copy the client ID and secret.
2. **Applications → Applications → Create**, slug e.g. `jellyfin`, using the
   provider above. The issuer is then
   `https://auth.example.org/application/o/jellyfin/` (shown on the provider
   page as "OpenID Configuration Issuer").
3. Create the groups you name in `JELLYFIN_OIDC_ALLOWED_GROUPS` and
   `JELLYFIN_OIDC_ADMIN_GROUPS` and add users to them. (You can also restrict
   access with a policy binding on the application; the fork's group check is
   independent of that.)

Authentik's default email mapping always sends `email_verified: false`. The
fork does not look at `email_verified`, so this does not matter.

## TVs and native apps: Quick Connect

Most Jellyfin apps cannot do an OIDC browser login. With password login
disabled they sign in with Quick Connect, which keeps working:

1. In the app on the TV, choose **Quick Connect**. It shows a six-character code.
2. In a browser, sign in to Jellyfin with OIDC, open the user menu →
   **Quick Connect**, enter the code and authorize.
3. The TV is signed in as you, with its own session.

Quick Connect must be enabled (Dashboard → General → "Enable Quick Connect";
it is on by default). API keys (Dashboard → API Keys) also keep working, for
Sonarr, Radarr, Seerr and similar tools.

## Break-glass

If the identity provider is down or misconfigured while password login is
disabled:

1. Existing sessions and API keys keep working; you may not need to do
   anything.
2. Otherwise, set `JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN=false` (or remove it)
   and restart the container. Log in with a local administrator's password.
   To get OIDC out of the way entirely, set `JELLYFIN_OIDC_ENABLED=false`.

For this to work you need at least one local administrator **with a
password**. Users created or linked by OIDC get a random password nobody
knows, so they cannot log in with a password unless an administrator sets one. Keep the administrator account from the first-run
wizard, with a strong password stored somewhere you can reach when SSO is down.

## Security notes

- **`JELLYFIN_OIDC_LINK_EXISTING_BY_USERNAME` is a migration aid.** Switch it
  on only while you move existing Jellyfin accounts to SSO, then off again
  (links, once made, persist). While it is on, the first OIDC login with
  username `bob` takes over the existing Jellyfin account `bob`, including an
  administrator account. Anyone who can pick or change their username at the
  identity provider (self-service sign-up, profile edits) can rename
  themselves to someone else's username and take over that account. On
  adoption the account's existing sessions and devices are revoked and its
  password is replaced with a random one.
- With link-by-username off, an existing local user with the same username
  blocks that identity-provider user's login until an administrator renames or
  removes the local account.
- **Revocation.** Removing someone at the identity provider, or from an
  allowed or admin group, does not end their existing Jellyfin sessions:
  Jellyfin tokens do not expire. To cut someone off, disable the user in
  Jellyfin (Dashboard → Users → the user → "Disable this user"). Admin
  demotion through `JELLYFIN_OIDC_ADMIN_GROUPS` applies at their next OIDC
  login.
- If you delete a Jellyfin user that is linked to an identity, that identity's
  next login is refused, not silently re-created. To let them in again as a
  new user, remove their entry from `oidc-links.json` (below) and restart.
- Group-based admin management (`JELLYFIN_OIDC_ADMIN_GROUPS`) re-evaluates on
  every login, including demotion. Anyone who can edit group membership at the
  identity provider controls who is a Jellyfin administrator.
- Disabling password login also disables forgot-password and password-change
  endpoints, for every user including administrators. Third-party plugins
  that check passwords themselves (for example LDAP authentication plugins)
  are not covered by it.
- The links between identity-provider subjects and Jellyfin users live in
  `<config dir>/oidc-links.json` (`/config/config/oidc-links.json` in the
  image). Back it up with the rest of `/config`.
- Use `JELLYFIN_OIDC_CLIENT_SECRET_FILE` for the client secret (see above).

### Moving back to stock Jellyfin

Change the image to `jellyfin/jellyfin:<same version>` and remove the
`JELLYFIN_OIDC_*` variables. There are no schema changes to undo. What changes:

- Users created or linked by OIDC still exist but have a random password; an
  administrator must set one for each before they can log in.
- `oidc-links.json` is ignored by stock Jellyfin; delete it or keep it in case
  you come back (links then still apply).
- The login button disappears. Your stored branding was never modified.

Moving to a stock image of a *newer* version is the normal Jellyfin upgrade
path; moving to an older version is not supported by Jellyfin either.

## How releases work

Every published image comes from a git tag `vX.Y-oidc.N` on branch
`oidc/vX.Y`, and only after the gate passed: the patch series applies, the
server builds, the fork's unit tests pass, and the end-to-end test passes
against the built image.

Image tags: `X.Y-oidc.N` (immutable), `X.Y` (newest fork release for that
upstream version), `latest` (newest upstream version), plus per-architecture
`X.Y-oidc.N-amd64` / `-arm64`. `linux/amd64` is the image the e2e suite ran
against, byte for byte. `linux/arm64` is cross-compiled from the same commit
and built, but not run-tested.

### Automated rebase

`.github/workflows/upstream-release.yml` runs every six hours (and on demand):

1. `release.py plan` finds the newest stable upstream release (GitHub
   releases, ignoring drafts, prereleases and RCs) and decides: nothing to do
   (already released), wait (the `jellyfin/jellyfin:X.Y` image is not on
   Docker Hub yet), replay (new upstream release), or release (a patch branch
   has commits that are not released yet: `N` is bumped).
2. `release.py replay` creates `oidc/v<new>` from upstream tag `v<new>` and
   cherry-picks the patch series `v<old>..oidc/v<old>` from the newest older
   patch branch. Upstream's `.github/` is never part of the series: each patch
   branch ends with one regenerated commit that deletes upstream's `.github/`
   and adds a small CI stub, and replay skips that commit (it carries the
   trailer `Jellyfin-Oidc-Automation: ci-stub`). Upstream changes to
   `.github/` therefore never conflict.
3. `release.py gate` builds the image, runs the fork's unit tests (the test
   classes in files the patch adds or changes under `tests/`) and runs
   `e2e/run.sh` against the image.
4. `release.py publish` pushes the branch and the release tag, pushes the
   images, and creates the GitHub release with a link to the
   upstream release and the list of patch commits. It refuses to run unless the
   gate passed on exactly this commit and image.

Every step is idempotent; re-running the workflow after a failure resumes.
Scheduled runs that find nothing to do finish in about a minute.

### When the replay conflicts

The replay stops, `release.py` exits with code 3 and writes `conflict.json`
(the commit that failed, the conflicting files, the commits still to apply).
Nothing is published from a conflicted or untested state.

- If the `CLAUDE_CODE_OAUTH_TOKEN` secret is configured, a job runs Claude
  (Claude Code, via `anthropics/claude-code-action`) on the conflict. It may
  edit files, run git (no push), run the gate, and must write a report. It has
  a read-only token and its checkouts carry no credentials, so it cannot push
  or publish. Afterwards the workflow imports only the resulting commits into
  a fresh job, re-runs the complete gate with a fresh copy of the automation
  and publishes only if that passes.
- Otherwise, or if anything fails, the workflow opens an issue titled
  "Release automation needs attention: upstream vX.Y" (or comments on the open
  one) with the conflict details, the run link and Claude's report if there is
  one. A later successful release closes it.

To resolve by hand:

```sh
git clone https://github.com/igor47/jellyfin-oidc.git && cd jellyfin-oidc
git worktree add ../jellyfin-oidc-main main          # the automation
R="python3 ../jellyfin-oidc-main/scripts/release.py --repo ."
$R plan --version 12.5          # same plan as the workflow
$R replay                       # stops at the conflict (exit 3)
# edit the files, git add them
$R continue                     # repeat until it exits 0
$R gate                         # build + unit tests + e2e
$R publish --dry-run            # see what would be pushed
$R publish                      # or push the branch and let the workflow
                                # (Actions -> upstream release -> branch: oidc/v12.5) publish
```

### Bootstrapping the repository

The first patch branch is pushed by hand. Two things matter:

- Push it with upstream's `.github/` already replaced, or upstream's
  workflows run in this repository on that push. `release.py` does that:

  ```sh
  R="python3 ../jellyfin-oidc-main/scripts/release.py --repo ."
  $R plan --branch oidc/v12.4 --skip-release-check   # local branch -> release N=1
  $R finalize                                       # adds the CI-stub commit
  git push origin main oidc/v12.4
  ```

- Do not push upstream's tags (`v12.4` etc.) to this repository one at a time.
  A pushed tag runs the workflows of the tagged commit, and upstream's
  `.github/` has a workflow triggered by `push: tags: v*`. The automation never
  needs them in this repository; it fetches them from upstream each run. The
  fork's own `vX.Y-oidc.N` tags point at commits that carry only the CI stub,
  so they are safe.

- A container package first pushed to ghcr.io with a personal token is created
  private. Make it public once, in the package's settings on GitHub ("Change
  visibility"); later pushes keep that setting.

Then run the "upstream release" workflow by hand (or wait for the schedule):
it gates and publishes `v12.4-oidc.1`.

### Re-releasing a patch branch

Commit the fix to `oidc/vX.Y` and push. `build.yml` gates it. Then run the
"upstream release" workflow by hand with `branch: oidc/vX.Y`; it releases
`vX.Y-oidc.N+1`. (A scheduled run also picks it up if `X.Y` is the newest
upstream release.)

### Secrets

| Secret | Needed | What |
|---|---|---|
| `RELEASE_PUSH_TOKEN` | yes, for publishing | Fine-grained personal access token for this repository only, with **Contents: read and write** and **Workflows: read and write**. `GITHUB_TOKEN` cannot push commits that touch `.github/workflows/`, and upstream's history between releases does. Without this secret the push fails and an issue is opened. |
| `CLAUDE_CODE_OAUTH_TOKEN` | optional | Lets the workflow try to resolve replay conflicts with Claude. Without it, conflicts open an issue. |

To add the Claude token: run `claude setup-token` on a machine logged in to
the Claude account that should pay for it, then in the repository go to
Settings → Secrets and variables → Actions → New repository secret, name
`CLAUDE_CODE_OAUTH_TOKEN`. The model is pinned in the workflow
(`--model claude-opus-5-5`).

Images are pushed with the workflow's own `GITHUB_TOKEN` (`packages: write`).
After the first push, make the package public once: Packages →
jellyfin-oidc → Package settings → Change visibility.

### Scheduled workflows and inactivity

GitHub disables scheduled workflows in public repositories after 60 days
without repository activity, and emails the owner shortly before. The
release workflow has a `keepalive` job that re-enables itself through the API
on every scheduled run, and every release pushes commits anyway. If it does
get disabled: Actions → upstream release → Enable workflow.

## Develop and test locally

Requirements: Docker with buildx and compose, Python 3, git, openssl. No .NET
SDK is needed; everything builds in containers.

```sh
# Build an image from a patch-branch checkout (working tree as is):
docker buildx build -f ~/src/jellyfin-oidc-main/Dockerfile \
  --build-arg UPSTREAM_VERSION=12.4 --build-arg FORK_VERSION=12.4-oidc.dev \
  -t jellyfin-oidc:dev --load ~/src/jellyfin-oidc

# Unit tests only (same container, test stage):
docker buildx build -f ~/src/jellyfin-oidc-main/Dockerfile --target test \
  --build-arg UPSTREAM_VERSION=12.4 \
  --build-arg TEST_PROJECTS=tests/Jellyfin.Api.Tests/Jellyfin.Api.Tests.csproj \
  --build-arg TEST_FILTER='FullyQualifiedName~Oidc' \
  --output type=cacheonly ~/src/jellyfin-oidc

# End-to-end test against an image (about 5 minutes):
e2e/run.sh jellyfin-oidc:dev
e2e/run.sh --keep --results /tmp/e2e jellyfin-oidc:dev     # keep the stack for debugging

# Negative control: the OIDC checks must fail against stock Jellyfin:
e2e/run.sh --negative-control jellyfin/jellyfin:12.4

# Whole release pipeline without publishing anything:
scripts/release.py --repo ~/src/jellyfin-oidc run --branch oidc/v12.4 --no-publish

# Replay logic in a scratch directory (add --with-build to also build + unit test):
scripts/test-replay.sh ~/src/jellyfin-oidc /tmp/replay-test
```

`release.py` never signs commits or tags (it passes `-c commit.gpgsign=false
-c tag.gpgsign=false`), so it does not depend on your git signing setup.

### The e2e suite

`e2e/run.sh` starts Keycloak with a realm (`e2e/keycloak/realm-e2e.json`):
users alice (groups jellyfin-users, jellyfin-admins), bob (jellyfin-users),
carol (no groups), dave and erin (jellyfin-users, and also pre-existing local
Jellyfin users), a confidential client that requires PKCE S256, and a
`groups` claim. Keycloak serves HTTPS with a throwaway CA that the Jellyfin
container trusts; the browser reaches Jellyfin as `http://localhost:8096`.
Then, in phases that restart Jellyfin with different settings on the same
data:

- **p1** (OIDC on, passwords on): first-run wizard via the API; the branding
  API carries the button and keeps the operator's branding (stored branding
  unchanged); the stock login page shows the button; a full browser login as
  alice lands in a working session (API calls succeed) with admin rights from
  her group; bob gets no admin rights, and is demoted again after an admin
  promotes him by hand; carol is denied and not created; dave is refused
  because a local `dave` exists (link-by-username off) and the local `dave`
  is untouched; OIDC users cannot log in with a password; an API key works.
- **p2** (password login disabled): `AuthenticateByName` (also for the
  administrator), the legacy authenticate route, forgot-password and
  password-change are rejected; the login page hides the password form and
  the forgot-password button; existing sessions and API keys keep working;
  OIDC login works; Quick Connect started by an unauthenticated "TV",
  approved from the OIDC session, yields a working token.
- **p3** (link-by-username on): erin's first OIDC login adopts the local
  `erin`, revoking the session she had before and invalidating her old
  password; existing links still win; after an admin deletes bob's account,
  bob's next OIDC login is refused and does not re-create him.
- **misconfig**: `ENABLED=true` without issuer or client, and with no allowed
  groups (and no `ALLOW_ANY_USER`), must not start.

Checks tagged `@base` must pass on stock Jellyfin too. They include a
self-test of the fixture (the IdP rejects requests without PKCE or without
the client secret; ID tokens carry the groups claim, fixed subjects and
`email_verified: false`) and a check that the Jellyfin container trusts the
IdP's certificate. Checks tagged `@oidc` must fail on stock Jellyfin;
`--negative-control` verifies exactly that, and that every `@base` check
passes there.

Results (Playwright JSON/JUnit reports, screenshots and traces of failures,
server logs per phase, `summary.txt`) go to `--results DIR`, or a temporary
directory whose path is printed at the end.

## License

The automation in this branch is licensed under the GNU General Public
License, version 2 (see [LICENSE](LICENSE)), to match Jellyfin. The server
source on the `oidc/*` branches is Jellyfin's, under its GPL-2.0 license, plus
the fork's patches under the same license.
