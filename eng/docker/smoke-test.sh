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
#   SMOKE_SDK_IMAGE  .NET SDK image of the NuGet client (default: the Dockerfile's build image)
#
# It checks the anonymous behaviour, creates an administrator with "admin bootstrap --password-stdin",
# a reader and a publisher, and two API keys of the publisher with "admin key create" (each key is read
# from the command's standard output). It pushes a package with the real dotnet CLI, the Publish key
# given to "dotnet nuget push --api-key" and the Read key as the source password, and restores it with
# the reader's password (eng/docker/smoke-client.sh, in an SDK container sharing the server's network
# namespace). It revokes the Publish key, restarts the container and restores again. Passwords and keys
# are random, never on a host command line, masked in GitHub Actions.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
# Git Bash on Windows: pass container paths to docker as they are, and host paths in Windows form.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
host_path() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

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

# Status line and headers of a request sent from the host, as a TLS-terminating proxy there would.
request() { # <path> [curl options...]
  local path=$1
  shift
  curl -sS -o /dev/null -D - --max-time 30 "$@" "$base$path" | tr -d '\r'
}
# Same with credentials, given to curl on standard input, never on its command line.
request_as() { # <user> <password> <path> [curl options...]
  local user=$1 password=$2 path=$3
  shift 3
  printf 'user = "%s:%s"\n' "$user" "$password" \
    | curl -sS -o /dev/null -D - --max-time 30 -K - -H 'X-Forwarded-Proto: https' "$@" "$base$path" | tr -d '\r'
}
# Same with an API key in the X-NuGet-ApiKey header, given to curl on standard input.
request_with_key() { # <key> <path> [curl options...]
  local key=$1 path=$2
  shift 2
  printf 'header = "X-NuGet-ApiKey: %s"\n' "$key" \
    | curl -sS -o /dev/null -D - --max-time 30 -K - -H 'X-Forwarded-Proto: https' "$@" "$base$path" | tr -d '\r'
}
status_of() { printf '%s\n' "$1" | sed -n '1s/^HTTP\/[0-9.]* \([0-9]*\).*/\1/p'; }
expect_status() { # <description> <expected status> <response headers>
  local actual
  actual=$(status_of "$3")
  if [ "$actual" = "$2" ]; then pass "$1: $2"; else fail "$1: expected $2, got ${actual:-no response}"; fi
}

new_password() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }
admin_password=$(new_password)
reader_password=$(new_password)
publisher_password=$(new_password)
if [ "${GITHUB_ACTIONS:-}" = true ]; then
  echo "::add-mask::$admin_password"
  echo "::add-mask::$reader_password"
  echo "::add-mask::$publisher_password"
fi

admin_cli() { docker exec -i "$name" dotnet ElPaqueto.dll admin "$@"; }

sdk_image=${SMOKE_SDK_IMAGE:-$(sed -n 's/^FROM --platform=\$BUILDPLATFORM \([^ ]*\) AS build$/\1/p' "$repo/Dockerfile")}
# The NuGet client. Passwords and keys reach it through the environment of the docker command, not its arguments.
run_client() { # push-and-restore | restore
  READER_USER=smoke-reader READER_PASSWORD=$reader_password \
    SOURCE_KEY=${read_key:-} PUBLISH_KEY=${publish_key:-} \
    docker run --rm --network "container:$name" \
    -e READER_USER -e READER_PASSWORD -e SOURCE_KEY -e PUBLISH_KEY \
    -v "$(host_path "$repo/eng/docker/smoke-client.sh"):/smoke-client.sh:ro" \
    "$sdk_image" sh /smoke-client.sh "$1"
}

# The host reaches a published port through the Docker bridge: trust that network as the proxy network,
# exactly as docs/user/reverse-proxy.md describes for a proxy on the Docker host. Security:PublicBaseUrl
# is not set, so that the URLs of the service index stay usable by the client inside the container.
bridge=$(docker network inspect bridge -f '{{(index .IPAM.Config 0).Subnet}}')

docker volume create "$volume" > /dev/null
docker run -d --name "$name" \
  --read-only --tmpfs /tmp \
  --cap-drop ALL --security-opt no-new-privileges \
  -v "$volume:/data" \
  -p "127.0.0.1:$port:8080" \
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

if [ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$name")" = true ] \
  && ! docker exec "$name" sh -c 'touch /home/app/smoke 2>/dev/null'; then
  pass "the root filesystem is read-only (even the user's own home folder)"
else
  fail "the root filesystem is writable"
fi
if docker exec "$name" sh -c 'touch /app/smoke 2>/dev/null'; then
  fail "the application folder is writable by the process"
else
  pass "the application folder is not writable by the process"
fi

# Liveness: 200 and the overall status only.
body=$(curl -sS --max-time 10 -H 'X-Forwarded-Proto: https' "$base/health")
if [ "$(printf '%s' "$body" | tr -d ' \n\r')" = '{"Status":"Healthy"}' ]; then
  pass "/health returns the overall status only"
else
  fail "/health returned an unexpected body: $body"
fi
expect_status "anonymous /health" 200 "$(request /health -H 'X-Forwarded-Proto: https')"

# Anonymous requests to protected endpoints.
headers=$(request /v3/index.json -H 'X-Forwarded-Proto: https')
if [ "$(status_of "$headers")" = "401" ] && printf '%s\n' "$headers" | grep -qx 'WWW-Authenticate: Basic realm="ElPaqueto"'; then
  pass "anonymous /v3/index.json returns 401 with the Basic challenge"
else
  fail "anonymous /v3/index.json: expected 401 with 'WWW-Authenticate: Basic realm=\"ElPaqueto\"', got: $headers"
fi
expect_status "anonymous /health/details" 401 "$(request /health/details -H 'X-Forwarded-Proto: https')"

# Plain HTTP from another machine (no forwarded scheme): refused without a credentials challenge.
headers=$(request /v3/index.json)
if [ "$(status_of "$headers")" = "403" ] && ! printf '%s\n' "$headers" | grep -qi '^WWW-Authenticate:'; then
  pass "remote plain HTTP is refused with 403 and no challenge"
else
  fail "remote plain HTTP: expected 403 without WWW-Authenticate, got: $headers"
fi

# Security database on the volume, owner-only.
db_stat=$(docker exec "$name" stat -c '%a %u' /data/db/auth.db 2> /dev/null || true)
if [ "$db_stat" = "600 $uid" ]; then
  pass "/data/db/auth.db exists, mode 600, owned by UID $uid"
else
  fail "/data/db/auth.db: expected mode 600 owned by $uid, got '${db_stat:-missing}'"
fi
inode_before=$(docker exec "$name" stat -c '%i' /data/db/auth.db)

# Accounts, created with the admin command inside the running container.
if printf '%s\n' "$admin_password" | admin_cli bootstrap --username smoke-admin --password-stdin; then
  pass "admin bootstrap created the first administrator"
else
  fail "admin bootstrap failed"
fi
code=0
printf '%s\n' "$admin_password" | admin_cli bootstrap --username smoke-admin-2 --password-stdin > /dev/null || code=$?
if [ "$code" = 3 ]; then pass "a second admin bootstrap is refused (exit code 3)"; else fail "a second admin bootstrap: expected exit code 3, got $code"; fi
printf '%s\n' "$reader_password" | admin_cli user create smoke-reader --permissions reader --password-stdin \
  || fail "creating the reader failed"
printf '%s\n' "$publisher_password" | admin_cli user create smoke-publisher --permissions publisher --password-stdin \
  || fail "creating the publisher failed"

expect_status "reader on /v3/index.json" 200 "$(request_as smoke-reader "$reader_password" /v3/index.json)"
expect_status "wrong password on /v3/index.json" 401 "$(request_as smoke-reader "x$reader_password" /v3/index.json)"
expect_status "reader on /health/details" 403 "$(request_as smoke-reader "$reader_password" /health/details)"
expect_status "administrator on /health/details" 200 "$(request_as smoke-admin "$admin_password" /health/details)"
expect_status "reader pushing a package" 403 "$(request_as smoke-reader "$reader_password" /api/v2/package -X PUT)"

# API keys of the publisher, created with the admin command: the key alone is on standard output, the
# messages on standard error.
key_pattern='^epq_[0-9A-Za-z]{12}_[0-9A-Za-z]{43}$'
publish_key=$(admin_cli key create --user smoke-publisher --name smoke-push --permissions publish --expires 1d < /dev/null) \
  || fail "admin key create (Publish key) failed"
read_key=$(admin_cli key create --user smoke-publisher --name smoke-source --permissions read --no-expiry < /dev/null) \
  || fail "admin key create (Read key) failed"
if [ "${GITHUB_ACTIONS:-}" = true ]; then
  echo "::add-mask::$publish_key"
  echo "::add-mask::$read_key"
fi
if [[ "$publish_key" =~ $key_pattern ]] && [[ "$read_key" =~ $key_pattern ]]; then
  pass "admin key create wrote exactly one key to standard output"
else
  fail "admin key create did not write exactly one key to standard output"
fi
key_list=$(admin_cli key list --include-revoked --format json < /dev/null) || fail "admin key list failed"
if [[ "$key_list" == *"${publish_key:17}"* ]] || [[ "$key_list" == *"${read_key:17}"* ]]; then
  fail "admin key list shows a secret"
else
  pass "admin key list shows the keys without their secrets"
fi
expect_status "Read key as the source password on /v3/index.json" 200 "$(request_as apikey "$read_key" /v3/index.json)"
expect_status "Publish-only key in X-NuGet-ApiKey on /v3/index.json" 403 "$(request_with_key "$publish_key" /v3/index.json)"
expect_status "Read key in X-NuGet-ApiKey pushing a package" 403 "$(request_with_key "$read_key" /api/v2/package -X PUT)"

# Push and restore with the real dotnet CLI.
if run_client push-and-restore; then
  pass "dotnet nuget push --api-key (Publish key) and dotnet restore (reader) with an empty package cache"
else
  fail "dotnet nuget push or dotnet restore failed"
fi

# Revocation takes effect on the next request.
if admin_cli key revoke "${publish_key:4:12}" < /dev/null; then
  pass "admin key revoke"
else
  fail "admin key revoke failed"
fi
expect_status "revoked Publish key pushing a package" 401 "$(request_with_key "$publish_key" /api/v2/package -X PUT)"

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
server_logs=$(docker logs "$name" 2>&1)
if [[ "$server_logs" == *"Application is shutting down"* ]]; then
  pass "the logs show a graceful shutdown"
else
  fail "the logs do not show a graceful shutdown"
fi

docker start "$name" > /dev/null
if ! wait_healthy; then
  fail "the container did not become healthy again after a restart"
  exit 1
fi
inode_after=$(docker exec "$name" stat -c '%i' /data/db/auth.db 2> /dev/null || true)
if [ "$inode_after" = "$inode_before" ]; then
  pass "the security database file survived the restart"
else
  fail "the security database file was not kept across the restart"
fi
expect_status "reader on /v3/index.json after the restart" 200 "$(request_as smoke-reader "$reader_password" /v3/index.json)"
if run_client restore; then
  pass "dotnet restore after the restart, with an empty package cache"
else
  fail "dotnet restore after the restart failed"
fi

if [ "$failures" -ne 0 ]; then
  log "$failures check(s) failed"
  exit 1
fi
log "smoke test passed"
