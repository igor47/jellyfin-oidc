# jellyfin-oidc: design contract

A public, GPL-2.0 fork of Jellyfin Server with OpenID Connect login built in
and a switch to turn password login off. Not affiliated with the Jellyfin
project. Owner: github.com/igor47. Image: `ghcr.io/igor47/jellyfin-oidc` (public).

## Principles

1. **Smallest possible patch on upstream release tags.** Every line that touches
   an upstream file is a future rebase conflict. New code lives in new files;
   upstream files get the minimum hook lines.
2. **Server-only.** jellyfin-web is taken unmodified from upstream. The login
   button and the hiding of the password form are done by the server through the
   existing branding API (login disclaimer HTML + custom CSS), and the callback
   is a server-rendered page.
3. **No database schema changes, no EF migrations.** A deployment must be able
   to move between this fork and stock Jellyfin of the same version. Fork state
   (OIDC subject -> user id links) lives in a JSON file in the config directory.
4. **Configuration is environment variables only.** No admin UI, no XML. The
   operator manages config declaratively in docker compose.
5. **Fail closed.** Any validation doubt in the OIDC flow rejects the login.

## Repository layout

One GitHub repo, `igor47/jellyfin-oidc`, standalone (not in the GitHub fork
network) but containing upstream history.

- `main` (default branch, orphan, never force-pushed): automation only.
  Workflows, rebase/release scripts, Dockerfile, e2e harness, README, this file.
  Local worktree: `/home/igor47/repos/jellyfin-oidc-automation`.
- `oidc/vX.Y` (one branch per upstream release): upstream tag `vX.Y` plus the
  fork's patch commits. Never force-pushed; a new upstream release gets a new
  branch made by replaying the patch series on the new tag.
  Local checkout: `/home/igor47/repos/jellyfin-oidc` (currently `oidc/v12.4`).
- Tags / releases: `vX.Y-oidc.N` (N starts at 1, bumps for fork-only fixes).
- Image tags: `X.Y-oidc.N`, `X.Y`, `latest`.
- Upstream's own `.github/` must not run in this repo: patch branches carry no
  upstream workflows (handled so that it never causes a rebase conflict).

## Server behaviour (the patch)

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `JELLYFIN_OIDC_ENABLED` | `false` | Master switch. |
| `JELLYFIN_OIDC_ISSUER` | | Issuer URL; discovery at `<issuer>/.well-known/openid-configuration`. |
| `JELLYFIN_OIDC_CLIENT_ID` | | |
| `JELLYFIN_OIDC_CLIENT_SECRET` | | Confidential client secret. |
| `JELLYFIN_OIDC_CLIENT_SECRET_FILE` | | Alternative: path to a file holding the secret. |
| `JELLYFIN_OIDC_SCOPES` | `openid profile email` | Space separated. |
| `JELLYFIN_OIDC_USERNAME_CLAIM` | `preferred_username` | Claim used as the Jellyfin username for new or linked users. |
| `JELLYFIN_OIDC_GROUPS_CLAIM` | `groups` | Claim holding group names (array or string). |
| `JELLYFIN_OIDC_ALLOWED_GROUPS` | (empty = everyone) | Comma separated. Login denied unless the user is in one. |
| `JELLYFIN_OIDC_ADMIN_GROUPS` | (empty = don't manage) | Comma separated. When set, administrator status is set on every login to match membership. |
| `JELLYFIN_OIDC_AUTO_CREATE_USERS` | `true` | Create a Jellyfin user on first login. |
| `JELLYFIN_OIDC_LINK_EXISTING_BY_USERNAME` | `false` | On first login, adopt an existing Jellyfin user with the same username (case-insensitive). Off by default: it is account takeover if IdP usernames are self-chosen. |
| `JELLYFIN_OIDC_PROVIDER_NAME` | `SSO` | Button label: "Sign in with <name>". |
| `JELLYFIN_OIDC_DISABLE_PASSWORD_LOGIN` | `false` | Reject all username/password authentication. |
| `JELLYFIN_OIDC_REDIRECT_BASE_URL` | (derived) | External base URL, e.g. `https://jellyfin.example.org`. If unset, use Jellyfin's published server URL, else the request's scheme/host. |

Invalid configuration with `ENABLED=true` (missing issuer or client) must be a
loud startup error, not a silent fallback to password login.

### Endpoints

- `GET /oidc/login` — starts the authorization code flow (PKCE S256, `state`,
  `nonce`), redirects to the IdP.
- `GET /oidc/callback` — validates state, exchanges the code, validates the ID
  token (signature via JWKS, issuer, audience, expiry, nonce), resolves or
  creates the user, creates a Jellyfin session, and returns a page that hands
  the session to the stock web client and navigates to it.
- Redirect URI registered at the IdP: `<base>/oidc/callback`.

### Login page (stock web client, unmodified)

`GET /Branding/Configuration` and `/Branding/Css` are augmented at response
time (the operator's stored branding is not modified):

- OIDC enabled: a "Sign in with <name>" button is prepended to the login
  disclaimer.
- Password login disabled: CSS hides the username/password form and the
  forgot-password button on the login page.

### Password login disabled means

- `POST /Users/AuthenticateByName` and the legacy per-user authenticate route
  are rejected, for every user including administrators.
- Forgot-password and password-change endpoints are rejected.
- Still working: OIDC, Quick Connect (TVs and native apps: start Quick Connect
  on the device, approve it from a browser session logged in through OIDC),
  API keys (needed by Sonarr/Radarr/Seerr etc.), existing sessions.
- Break-glass: unset the variable and restart.

### Users

- Identity key is (`iss`, `sub`), stored in `<config dir>/oidc-links.json`.
- Users created or linked by OIDC cannot log in with a password even when
  password login is enabled, unless an administrator sets one.
- Do not depend on `email_verified`. (Authentik's default email mapping
  hardcodes it to false.)

## Gate for automated releases

A release is published only if: the patch series applies, the server builds,
the fork's unit tests pass, and the end-to-end test passes. The end-to-end test
runs the built image against a throwaway OIDC provider and drives the **stock
web client in a real headless browser**: button visible, full login completes,
the session works, password login is rejected when disabled, Quick Connect
works when password login is disabled, an API key works.
