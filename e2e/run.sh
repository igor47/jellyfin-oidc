#!/usr/bin/env bash
# End-to-end gate for jellyfin-oidc images.
#
#   e2e/run.sh [--negative-control] [--keep] [--results DIR] <image>
#
# Brings up a throwaway Keycloak (HTTPS, private CA) with five users, two
# groups and a confidential client, runs the given Jellyfin image against it,
# completes the first-run wizard through the API and drives the stock web
# client with headless Chromium (Playwright), in phases:
#
#   p1  OIDC on, password login on, link-by-username off, allowed + admin groups
#   p2  same data, DISABLE_PASSWORD_LOGIN=true
#   p3  same data, LINK_EXISTING_BY_USERNAME=true
#   misconfig  ENABLED=true without issuer/client must fail at startup
#
# Exit status: 0 = gate passed; 1 = gate failed (see the summary);
# 2 = usage error; 3 = the harness itself could not run (docker, images, ...).
#
# --negative-control inverts the verdict for a stock image: it passes only if
# every @base check passes and every @oidc check fails, i.e. it proves the
# suite can detect a server without the patch. Use it with
# jellyfin/jellyfin:<version>.
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
MODE=normal
KEEP=0
RESULTS=""
IMAGE=""

usage() { sed -n '2,/^set -euo/p' "$0" | sed -e 's/^# \{0,1\}//' -e '/^set -euo/d' >&2; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --negative-control) MODE=negative; shift ;;
    --keep) KEEP=1; shift ;;
    --results) RESULTS=$2; shift 2 ;;
    -h|--help) usage ;;
    -*) echo "unknown option $1" >&2; usage ;;
    *) [[ -z "$IMAGE" ]] || usage; IMAGE=$1; shift ;;
  esac
done
[[ -n "$IMAGE" ]] || usage

log() { printf '\n=== %s\n' "$*" >&2; }
die() { echo "e2e: $*" >&2; exit 3; }

command -v docker >/dev/null || die "docker not found"
command -v openssl >/dev/null || die "openssl not found"
docker compose version >/dev/null || die "docker compose plugin not found"

WORK=$(mktemp -d "${E2E_TMPDIR:-${TMPDIR:-/tmp}}/jellyfin-oidc-e2e.XXXXXX")
[[ -n "$RESULTS" ]] || RESULTS="$WORK/results"
mkdir -p "$RESULTS" "$WORK/certs"
RESULTS=$(cd "$RESULTS" && pwd)
rm -f "$RESULTS"/{p1,p2,p3}.json "$RESULTS"/misconfig.txt "$RESULTS"/tls.txt "$RESULTS"/state.json

E2E_PROJECT="jfoidc-e2e-$(basename "$WORK" | tr -dc '[:alnum:]' | tr '[:upper:]' '[:lower:]')"
export E2E_PROJECT
export E2E_WORKDIR="$WORK"
export E2E_RESULTS_DIR="$RESULTS"
export E2E_JELLYFIN_IMAGE="$IMAGE"
export E2E_UID E2E_GID
E2E_UID=$(id -u); E2E_GID=$(id -g)

dc() { docker compose -f "$HERE/compose.yml" --project-directory "$HERE" "$@"; }

# shellcheck disable=SC2329 # invoked by the EXIT trap
cleanup() {
  local rc=$?
  if [[ $KEEP -eq 1 ]]; then
    echo "e2e: --keep: stack '$E2E_PROJECT' left running; tear down with:" >&2
    echo "  E2E_PROJECT=$E2E_PROJECT E2E_WORKDIR=$WORK E2E_RESULTS_DIR=$RESULTS E2E_JELLYFIN_IMAGE=$IMAGE E2E_RUNNER_IMAGE=${E2E_RUNNER_IMAGE:-x} docker compose -f $HERE/compose.yml --profile runner down -v && rm -r $WORK" >&2
  else
    dc --profile runner down -v --remove-orphans >/dev/null 2>&1 || echo "e2e: warning: compose down failed for $E2E_PROJECT" >&2
    rm -rf "$WORK/certs" "$WORK/client-secret"
    # The work dir only remains if the results live inside it.
    if [[ "$RESULTS" != "$WORK"/* ]]; then rmdir "$WORK"; fi
  fi
  echo "e2e: results in $RESULTS" >&2
  exit "$rc"
}
trap cleanup EXIT

log "image under test: $IMAGE (mode: $MODE)"
if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  docker pull "$IMAGE" || die "cannot pull $IMAGE"
fi
docker image inspect "$IMAGE" --format 'labels: {{json .Config.Labels}}' >&2

# --- throwaway PKI: a CA, and a cert for the IdP's hostname "idp"
log "generating throwaway CA and IdP certificate"
(
  cd "$WORK/certs"
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=jellyfin-oidc e2e CA" \
    -keyout ca.key -out ca.crt 2>/dev/null
  openssl req -newkey rsa:2048 -nodes -subj "/CN=idp" -keyout idp.key -out idp.csr 2>/dev/null
  printf 'subjectAltName=DNS:idp\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\n' > ext.cnf
  openssl x509 -req -in idp.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 2 \
    -extfile ext.cnf -out idp.crt 2>/dev/null
  chmod 644 idp.key   # read by keycloak's non-root user; throwaway key
  rm -f ca.key idp.csr ext.cnf ca.srl
)
# The image's own CA bundle plus our CA, mounted over the system bundle.
docker run --rm --entrypoint cat "$IMAGE" /etc/ssl/certs/ca-certificates.crt > "$WORK/certs/bundle.crt" \
  || die "could not read the CA bundle from $IMAGE"
cat "$WORK/certs/ca.crt" >> "$WORK/certs/bundle.crt"
printf '%s' 'e2e-client-secret' > "$WORK/client-secret"
chmod 644 "$WORK/client-secret"

# --- runner image, tagged by content so it is rebuilt only when it changes
RUNNER_HASH=$(cd "$HERE/runner" && find . -type f ! -path './node_modules/*' -print0 | sort -z | xargs -0 sha256sum | sha256sum | cut -c1-12)
export E2E_RUNNER_IMAGE="jellyfin-oidc-e2e-runner:$RUNNER_HASH"
if ! docker image inspect "$E2E_RUNNER_IMAGE" >/dev/null 2>&1; then
  log "building runner image $E2E_RUNNER_IMAGE"
  docker build -q -t "$E2E_RUNNER_IMAGE" "$HERE/runner" >/dev/null || die "runner image build failed"
fi

log "starting IdP"
dc up -d netns idp || die "compose up failed"

save_logs() {
  dc logs --no-color jellyfin > "$RESULTS/jellyfin-$1.log" 2>&1 || true
}

# Run one Playwright phase. Playwright exits 0 (all passed) or 1 (some
# failed); anything else is an infrastructure error and is recorded as such.
run_phase() {
  local phase=$1 spec=$2
  log "phase $phase: jellyfin env OIDC_ENABLED=${E2E_OIDC_ENABLED:-true} DISABLE_PASSWORD=${E2E_DISABLE_PASSWORD:-false} LINK_EXISTING=${E2E_LINK_EXISTING:-false}"
  dc up -d --force-recreate --no-deps jellyfin || die "could not (re)start jellyfin for $phase"
  local rc=0
  dc run --rm --no-deps -T --user "$E2E_UID:$E2E_GID" -e HOME=/tmp -e E2E_PHASE="$phase" runner \
    npx playwright test "tests/$spec" || rc=$?
  save_logs "$phase"
  case $rc in
    0) echo "phase $phase: all tests passed" >&2 ;;
    1) echo "phase $phase: some tests failed" >&2 ;;
    *) echo "phase $phase: runner exited $rc (infrastructure error)" >&2 ;;
  esac
}

export E2E_OIDC_ENABLED=true E2E_DISABLE_PASSWORD=false E2E_LINK_EXISTING=false
run_phase p1 p1-oidc.spec.ts
# The server must trust the IdP's certificate (bundle mounted over the system
# one); checked with the image's own curl/OpenSSL.
if dc exec -T jellyfin curl -fsS -o /dev/null https://idp:8443/realms/e2e/.well-known/openid-configuration; then
  echo "pass" > "$RESULTS/tls.txt"
else
  echo "fail: jellyfin container cannot verify https://idp:8443" | tee "$RESULTS/tls.txt" >&2
fi
export E2E_DISABLE_PASSWORD=true
run_phase p2 p2-nopassword.spec.ts
export E2E_DISABLE_PASSWORD=false E2E_LINK_EXISTING=true
run_phase p3 p3-link.spec.ts

# --- misconfiguration: ENABLED=true without issuer and client id must not
# start (or at least must never become healthy and serve logins).
log "phase misconfig: OIDC enabled without issuer/client"
export E2E_OIDC_ISSUER="" E2E_OIDC_CLIENT_ID="" E2E_LINK_EXISTING=false
dc up -d --force-recreate --no-deps jellyfin || die "could not start jellyfin for misconfig"
CID=$(dc ps -a -q jellyfin)
verdict=""
for _ in $(seq 1 45); do
  state=$(docker inspect -f '{{.State.Status}} {{.State.ExitCode}}' "$CID")
  if [[ $state == exited* ]]; then
    code=${state#exited }
    if [[ $code != 0 ]]; then verdict="pass: server refused to start (exit $code)"; else verdict="fail: server exited 0"; fi
    break
  fi
  if dc exec -T jellyfin curl -fsS -o /dev/null http://localhost:8096/health 2>/dev/null; then
    verdict="fail: server started and reports healthy despite invalid OIDC config"
    break
  fi
  sleep 2
done
[[ -n "$verdict" ]] || verdict="pass: server did not become healthy within 90s"
echo "$verdict" | tee "$RESULTS/misconfig.txt" >&2
save_logs misconfig
dc logs --no-color idp > "$RESULTS/idp.log" 2>&1 || true

log "summary"
src=0
dc run --rm --no-deps -T --user "$E2E_UID:$E2E_GID" -e HOME=/tmp runner \
  node summarize.mjs /results "$MODE" | tee "$RESULTS/summary.txt" || src=$?
case $src in
  0) exit 0 ;;
  1) exit 1 ;;
  *) die "summary step failed (exit $src)" ;;
esac
