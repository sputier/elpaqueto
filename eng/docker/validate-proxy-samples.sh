#!/usr/bin/env bash
# Syntax checks of the Compose example and the reverse-proxy samples (samples/reverse-proxy/).
# Used by the Docker workflow; runs locally the same way (needs docker and openssl).
#
#   eng/docker/validate-proxy-samples.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

# Proxy images used for the checks, pinned to a release.
NGINX_IMAGE=nginx:1.30.6-alpine
CADDY_IMAGE=caddy:2.11.9-alpine

# Git Bash on Windows: pass container paths to docker as they are, and host paths in Windows form.
docker() { MSYS_NO_PATHCONV=1 command docker "$@"; }
host_path() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "==> docker compose config (docker-compose.example.yml)"
# The secret file is git-ignored; Compose only needs a path to resolve.
ELPAQUETO_IMAGE=elpaqueto:validate docker compose -f docker-compose.example.yml config -q
echo "ok"

echo "==> nginx -t (samples/reverse-proxy/nginx/elpaqueto.conf)"
# A throwaway self-signed certificate, and a fixed address for the upstream host name (nginx resolves it
# when it loads the configuration). 192.0.2.10 is a documentation address.
MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=nuget.example.test" \
  -keyout "$(host_path "$work/nuget.example.test.key")" -out "$(host_path "$work/nuget.example.test.crt")" \
  2> "$work/openssl.log" || { cat "$work/openssl.log" >&2; exit 1; }
chmod 0644 "$work/nuget.example.test.key"
docker run --rm \
  --add-host elpaqueto:192.0.2.10 \
  -v "$(host_path "$PWD/samples/reverse-proxy/nginx/elpaqueto.conf"):/etc/nginx/conf.d/default.conf:ro" \
  -v "$(host_path "$work"):/etc/nginx/certs:ro" \
  "$NGINX_IMAGE" nginx -t

echo "==> caddy validate (samples/reverse-proxy/caddy/Caddyfile)"
docker run --rm \
  -v "$(host_path "$PWD/samples/reverse-proxy/caddy/Caddyfile"):/etc/caddy/Caddyfile:ro" \
  "$CADDY_IMAGE" caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

echo "==> docker compose config (samples/reverse-proxy/traefik/compose.yml)"
docker compose -f samples/reverse-proxy/traefik/compose.yml config -q
echo "ok"

echo "all samples are valid"
