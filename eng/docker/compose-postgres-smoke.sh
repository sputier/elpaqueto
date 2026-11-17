#!/usr/bin/env bash
# End-to-end check of samples/compose-postgres: builds the image from this repository, brings the stack up
# (ElPaqueto, PostgreSQL, Caddy), creates users and keys with the admin commands, pushes and restores a package
# with the real dotnet CLI, checks that the package is in PostgreSQL and that the data survives a restart and a
# "down" / "up". Used by the CI workflow (job postgresql); runs locally the same way (needs docker with compose).
#
#   CHOWN=0 eng/docker/compose-postgres-smoke.sh      # Docker Desktop: no chown of the secret files
#
# Environment:
#   SMOKE_HTTPS_PORT, SMOKE_HTTP_PORT   host ports of Caddy (default 18443 and 18081)
#   CHOWN                               0 skips the chown of the secret files (see init-secrets.sh)
#   SMOKE_SDK_IMAGE                     .NET SDK image of the NuGet client (default: the Dockerfile's build image)
#
# TLS is real: Caddy serves "localhost" with its own local CA (the checks use curl -k). The dotnet CLI runs in an SDK
# container that shares the network namespace of the ElPaqueto container and reaches it over loopback, the way
# eng/docker/smoke-test.sh does, because the local CA is not trusted by the client.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
sample="$repo/samples/compose-postgres"
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
host_path() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

https_port=${SMOKE_HTTPS_PORT:-18443}
http_port=${SMOKE_HTTP_PORT:-18081}
work=$(mktemp -d)
failures=0

export COMPOSE_PROJECT_NAME="epq-pg-smoke-$$"
export SECRETS_DIR="$work/secrets"
export NUGET_HOST=localhost
export HTTPS_PORT=$https_port
export HTTP_PORT=$http_port
export ELPAQUETO_PUBLIC_URL="https://localhost:$https_port"
export PROXY_SUBNET=192.0.2.0/24
export ELPAQUETO_IMAGE=elpaqueto:compose-smoke
compose() { docker compose -f "$sample/compose.yml" "$@"; }

log() { printf '%s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; failures=$((failures + 1)); }
pass() { printf 'ok: %s\n' "$*"; }

cleanup() {
  local status=$?
  if [ "$status" -ne 0 ] || [ "$failures" -ne 0 ]; then
    compose ps -a 2>&1 || true
    compose logs --no-color --tail 80 2>&1 || true
  fi
  compose down -v --remove-orphans > /dev/null 2>&1 || true
  docker image rm -f "$ELPAQUETO_IMAGE" > /dev/null 2>&1 || true
  # The files of the secrets folder may belong to other users.
  rm -rf "$work" 2> /dev/null || sudo rm -rf "$work" 2> /dev/null || true
}
trap cleanup EXIT

new_password() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }
reader_password=$(new_password)
publisher_password=$(new_password)
admin_password=$(new_password)
if [ "${GITHUB_ACTIONS:-}" = true ]; then
  echo "::add-mask::$reader_password"
  echo "::add-mask::$publisher_password"
  echo "::add-mask::$admin_password"
fi

admin_cli() { compose exec -T elpaqueto dotnet ElPaqueto.dll admin "$@"; }
status_of() { sed -n '1s/^HTTP\/[0-9.]* \([0-9]*\).*/\1/p'; }
# Status of a request through Caddy (TLS), credentials given to curl on standard input.
through_caddy() { # <user> <password> <path> [curl options...]
  local user=$1 password=$2 path=$3
  shift 3
  printf 'user = "%s:%s"\n' "$user" "$password" \
    | curl -sk -o /dev/null -D - --max-time 30 -K - "$@" "https://localhost:$https_port$path" | tr -d '\r' | status_of
}
expect() { if [ "$2" = "$3" ]; then pass "$1: $2"; else fail "$1: expected $2, got ${3:-no response}"; fi; }

wait_for_health() { # until the elpaqueto container reports healthy
  local deadline=$((SECONDS + 180)) id status
  while [ "$SECONDS" -lt "$deadline" ]; do
    id=$(compose ps -q elpaqueto)
    status=$(docker inspect -f '{{.State.Status}}/{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id")
    [ "$status" = "running/healthy" ] && return 0
    sleep 2
  done
  log "elpaqueto is not healthy: $status"
  return 1
}

sdk_image=${SMOKE_SDK_IMAGE:-$(sed -n 's/^FROM --platform=\$BUILDPLATFORM \([^ ]*\) AS build$/\1/p' "$repo/Dockerfile")}
run_client() { # push-and-restore | restore
  READER_USER=smoke-reader READER_PASSWORD=$reader_password SOURCE_KEY=${read_key:-} PUBLISH_KEY=${publish_key:-} \
    docker run --rm --network "container:$(compose ps -q elpaqueto)" \
    -e READER_USER -e READER_PASSWORD -e SOURCE_KEY -e PUBLISH_KEY \
    -v "$(host_path "$repo/eng/docker/smoke-client.sh"):/smoke-client.sh:ro" \
    "$sdk_image" sh /smoke-client.sh "$1"
}

log "==> secrets"
bash "$sample/init-secrets.sh"
printf '%s' "$admin_password" > "$work/secrets/admin_password.txt"

log "==> docker compose up --build --wait"
compose up -d --build --wait --wait-timeout 600
pass "the stack is up and healthy (postgres, elpaqueto, caddy)"

# Hardening of the three services, from what Docker applied.
for service in postgres elpaqueto caddy; do
  id=$(compose ps -q "$service")
  read_only=$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$id")
  caps=$(docker inspect -f '{{.HostConfig.CapDrop}}' "$id")
  security=$(docker inspect -f '{{.HostConfig.SecurityOpt}}' "$id")
  if [ "$read_only" = true ] && [[ "$caps" == *ALL* ]] && [[ "$security" == *no-new-privileges* ]]; then
    pass "$service: read-only root filesystem, all capabilities dropped, no-new-privileges"
  else
    fail "$service: read_only=$read_only cap_drop=$caps security_opt=$security"
  fi
done

# The connection string is a secret file, in no environment variable and no image layer setting.
if docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$(compose ps -q elpaqueto)" | grep -qi 'password'; then
  fail "a password appears in the environment of the elpaqueto container"
else
  pass "no password in the environment of the elpaqueto container"
fi
if [ "$(compose exec -T elpaqueto sh -c 'ls /run/secrets' | tr -d '\r' | grep -c '^Database__ConnectionString$')" = 1 ]; then
  pass "the connection string is the secret file /run/secrets/Database__ConnectionString"
else
  fail "/run/secrets/Database__ConnectionString is missing"
fi

# The server is on PostgreSQL, and its migrations ran there.
psql() { compose exec -T postgres psql -U elpaqueto -d elpaqueto -tA -c "$1" | tr -d '\r'; }
expect "migrations recorded in PostgreSQL" t "$([ "$(psql 'SELECT count(*) FROM "__EFMigrationsHistory"')" -ge 9 ] && echo t || echo f)"
# The security database stays SQLite on the data volume.
if compose exec -T elpaqueto test -f /data/db/auth.db; then pass "auth.db (SQLite) is on the data volume"; else fail "auth.db is missing"; fi

# Through the proxy, over TLS.
expect "anonymous /health through Caddy" 200 "$(curl -sk -o /dev/null -D - --max-time 30 "https://localhost:$https_port/health" | tr -d '\r' | status_of)"
expect "anonymous /v3/index.json through Caddy" 401 "$(curl -sk -o /dev/null -D - --max-time 30 "https://localhost:$https_port/v3/index.json" | tr -d '\r' | status_of)"
expect "plain HTTP through Caddy" 404 "$(curl -s -o /dev/null -D - --max-time 30 "http://localhost:$http_port/v3/index.json" | tr -d '\r' | status_of)"

log "==> accounts"
printf '%s\n' "$admin_password" | admin_cli bootstrap --username smoke-admin --password-stdin && pass "admin bootstrap" || fail "admin bootstrap"
printf '%s\n' "$reader_password" | admin_cli user create smoke-reader --permissions reader --password-stdin || fail "reader"
printf '%s\n' "$publisher_password" | admin_cli user create smoke-publisher --permissions publisher --password-stdin || fail "publisher"
publish_key=$(admin_cli key create --user smoke-publisher --name smoke-push --permissions publish --expires 1d < /dev/null) || fail "publish key"
read_key=$(admin_cli key create --user smoke-publisher --name smoke-source --permissions read --no-expiry < /dev/null) || fail "read key"
if [ "${GITHUB_ACTIONS:-}" = true ]; then
  echo "::add-mask::$publish_key"
  echo "::add-mask::$read_key"
fi
expect "reader through Caddy (TLS)" 200 "$(through_caddy smoke-reader "$reader_password" /v3/index.json)"
expect "wrong password through Caddy" 401 "$(through_caddy smoke-reader "x$reader_password" /v3/index.json)"
expect "administrator on /health/details through Caddy" 200 "$(through_caddy smoke-admin "$admin_password" /health/details)"
expect "reader on /health/details through Caddy" 403 "$(through_caddy smoke-reader "$reader_password" /health/details)"

log "==> push and restore with the dotnet CLI"
if run_client push-and-restore; then pass "dotnet nuget push and dotnet restore"; else fail "dotnet nuget push or restore failed"; fi
row=$(psql 'SELECT "Id" || '"'"'|'"'"' || "Version" FROM "Packages"')
expect "the package is a row of PostgreSQL" "ElPaqueto.Smoke.Package|1.0.0" "$row"
expect "the package is visible through Caddy" 200 "$(through_caddy smoke-reader "$reader_password" /v3/package/elpaqueto.smoke.package/index.json)"

log "==> restart, then down and up (volumes kept)"
compose restart elpaqueto > /dev/null
wait_for_health
if run_client restore; then pass "restore after a restart of the server"; else fail "restore after a restart"; fi
compose down > /dev/null
compose up -d --wait --wait-timeout 300 > /dev/null
if run_client restore; then pass "restore after down and up (PostgreSQL and data volumes kept)"; else fail "restore after down and up"; fi
expect "package rows after down and up" 1 "$(psql 'SELECT count(*) FROM "Packages"')"

if [ "$failures" -ne 0 ]; then
  log "$failures check(s) failed"
  exit 1
fi
log "all checks passed"
