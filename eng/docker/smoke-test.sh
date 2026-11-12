#!/usr/bin/env bash
# Container smoke test: runs an ElPaqueto image the way the documentation recommends and checks
# its runtime contract. Used by the Docker workflow; runs locally the same way.
#
# Usage: eng/docker/smoke-test.sh <image>
#
# Environment:
#   SMOKE_PORT       host port published on 127.0.0.1 (default 18080)
#   SMOKE_LOG_DIR    folder that receives the container logs on failure (default: none)
#   HEALTH_TIMEOUT   seconds to wait for the "healthy" status (default 120)
#
# The test needs no account: it checks anonymous behaviour only.
# TODO(#46): once the admin CLI (#20) and API-key authentication are merged, extend it with
# "admin bootstrap --password-stdin", a Publish API key and a Read user, a real "dotnet nuget push"
# and "dotnet restore" (isolated NUGET_PACKAGES), and the same restore after "docker restart".
set -euo pipefail
# Git Bash on Windows: pass container paths to docker as they are.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }

image=${1:?usage: smoke-test.sh <image>}
port=${SMOKE_PORT:-18080}
health_timeout=${HEALTH_TIMEOUT:-120}
name="elpaqueto-smoke-$$"
volume="elpaqueto-smoke-data-$$"
base="http://127.0.0.1:$port"
failures=0

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; failures=$((failures + 1)); }
pass() { printf 'ok: %s\n' "$*"; }

cleanup() {
  local status=$?
  if [ "$status" -ne 0 ] || [ "$failures" -ne 0 ]; then
    if [ -n "${SMOKE_LOG_DIR:-}" ]; then
      mkdir -p "$SMOKE_LOG_DIR"
      docker logs "$name" > "$SMOKE_LOG_DIR/container.log" 2>&1 || true
      docker inspect "$name" > "$SMOKE_LOG_DIR/container-inspect.json" 2>&1 || true
    else
      docker logs --tail 200 "$name" 2>&1 || true
    fi
  fi
  docker rm -f "$name" > /dev/null 2>&1 || true
  docker volume rm -f "$volume" > /dev/null 2>&1 || true
}
trap cleanup EXIT

wait_healthy() {
  local deadline=$((SECONDS + health_timeout)) status
  while [ "$SECONDS" -lt "$deadline" ]; do
    status=$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$name")
    case "$status" in
      running/healthy) return 0 ;;
      exited/* | dead/*) log "container stopped: $status"; return 1 ;;
    esac
    sleep 2
  done
  log "not healthy after ${health_timeout}s: $status"
  return 1
}

# Status code and headers of a request sent as a TLS-terminating proxy on the host would send it.
request() { # <path> [curl options...]
  local path=$1
  shift
  curl -sS -o /dev/null -D - --max-time 10 "$@" "$base$path" | tr -d '\r'
}
status_of() { printf '%s\n' "$1" | sed -n '1s/^HTTP\/[0-9.]* \([0-9]*\).*/\1/p'; }

# The host reaches a published port through the Docker bridge: trust that network as the proxy network,
# exactly as docs/user/reverse-proxy.md describes for a proxy on the Docker host.
bridge=$(docker network inspect bridge -f '{{(index .IPAM.Config 0).Subnet}}')

docker volume create "$volume" > /dev/null
docker run -d --name "$name" \
  --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges \
  -v "$volume:/data" \
  -p "127.0.0.1:$port:8080" \
  -e Security__PublicBaseUrl=https://nuget.example.test \
  -e "Security__ReverseProxy__KnownNetworks__0=$bridge" \
  "$image" > /dev/null

started=$SECONDS
if ! wait_healthy; then
  fail "the container never became healthy"
  exit 1
fi
pass "healthy after $((SECONDS - started))s (read-only root, all capabilities dropped, no-new-privileges)"

uid=$(docker exec "$name" id -u)
if [ "$uid" = "0" ]; then fail "the process runs as root"; else pass "runs as UID $uid"; fi

if docker exec "$name" sh -c 'touch /app/smoke 2>/dev/null'; then
  fail "the application folder is writable"
else
  pass "the root filesystem is read-only"
fi

# Liveness: 200 and the overall status only.
body=$(curl -sS --max-time 10 -H 'X-Forwarded-Proto: https' "$base/health")
if [ "$(printf '%s' "$body" | tr -d ' \n\r')" = '{"Status":"Healthy"}' ]; then
  pass "/health returns the overall status only"
else
  fail "/health returned an unexpected body: $body"
fi
headers=$(request /health -H 'X-Forwarded-Proto: https')
[ "$(status_of "$headers")" = "200" ] && pass "/health returns 200" || fail "/health: expected 200, got $(status_of "$headers")"

# Anonymous requests to protected endpoints.
headers=$(request /v3/index.json -H 'X-Forwarded-Proto: https')
if [ "$(status_of "$headers")" = "401" ] && printf '%s\n' "$headers" | grep -qx 'WWW-Authenticate: Basic realm="ElPaqueto"'; then
  pass "anonymous /v3/index.json returns 401 with the Basic challenge"
else
  fail "anonymous /v3/index.json: expected 401 with 'WWW-Authenticate: Basic realm=\"ElPaqueto\"', got: $headers"
fi
headers=$(request /health/details -H 'X-Forwarded-Proto: https')
[ "$(status_of "$headers")" = "401" ] && pass "anonymous /health/details returns 401" \
  || fail "anonymous /health/details: expected 401, got $(status_of "$headers")"

# Plain HTTP from another machine (no forwarded scheme): refused without a credentials challenge.
headers=$(request /v3/index.json)
if [ "$(status_of "$headers")" = "403" ] && ! printf '%s\n' "$headers" | grep -qi '^WWW-Authenticate:'; then
  pass "remote plain HTTP is refused with 403 and no challenge"
else
  fail "remote plain HTTP: expected 403 without WWW-Authenticate, got: $headers"
fi

# Security database on the volume, owner-only.
db_stat=$(docker exec "$name" stat -c '%a %u' /data/db/auth.db 2>/dev/null || true)
if [ "$db_stat" = "600 $uid" ]; then
  pass "/data/db/auth.db exists, mode 600, owned by UID $uid"
else
  fail "/data/db/auth.db: expected mode 600 owned by $uid, got '${db_stat:-missing}'"
fi
inode_before=$(docker exec "$name" stat -c '%i' /data/db/auth.db)

# Graceful stop within the 10 s grace period, then restart on the same volume.
stop_started=$SECONDS
docker stop -t 10 "$name" > /dev/null
stop_seconds=$((SECONDS - stop_started))
exit_code=$(docker inspect -f '{{.State.ExitCode}}' "$name")
if [ "$exit_code" = "0" ] && [ "$stop_seconds" -lt 10 ]; then
  pass "docker stop: graceful exit (code 0) in ${stop_seconds}s"
else
  fail "docker stop: exit code $exit_code after ${stop_seconds}s (expected 0, under 10s)"
fi
if docker logs "$name" 2>&1 | grep -q 'Application is shutting down'; then
  pass "the logs show a graceful shutdown"
else
  fail "the logs do not show a graceful shutdown"
fi

docker start "$name" > /dev/null
if ! wait_healthy; then
  fail "the container did not become healthy again after a restart"
  exit 1
fi
inode_after=$(docker exec "$name" stat -c '%i' /data/db/auth.db 2>/dev/null || true)
if [ "$inode_after" = "$inode_before" ]; then
  pass "the security database survived the restart"
else
  fail "the security database was not kept across the restart"
fi

if [ "$failures" -ne 0 ]; then
  log "$failures check(s) failed"
  exit 1
fi
log "smoke test passed"
