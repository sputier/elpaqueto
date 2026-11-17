#!/usr/bin/env bash
# Creates the secret files of the PostgreSQL Compose sample in ./secrets (git-ignored), owner-only:
#   postgres_password            the password of the database user (read by PostgreSQL, UID 70)
#   database_connection_string   the Npgsql connection string, with the same password (read by ElPaqueto, UID 1654)
#   admin_password.txt           a password for the first administrator (read once by "admin bootstrap")
# A file that already exists is never overwritten. Passwords are random and never printed.
#
# Compose mounts a file secret as it is on the host (without Swarm, a secret's uid, gid and mode are ignored), so
# each file must belong to the user that reads it. The script uses chown, through sudo when it is not root; with
# CHOWN=0 it skips that and makes the files world-readable (a local test on a machine that nobody else uses).
set -euo pipefail
cd "$(dirname "$0")"

pool=${MAX_POOL_SIZE:-20}
dir=${SECRETS_DIR:-secrets}
mkdir -p "$dir"
chmod 0700 "$dir"

random() { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'; }

give() { # <file> <uid> <mode>
  if [ "${CHOWN:-1}" = 0 ]; then
    chmod 0444 "$1"
  elif ! chown "$2:$2" "$1" 2> /dev/null; then
    sudo chown "$2:$2" "$1"
    chmod "$3" "$1"
  else
    chmod "$3" "$1"
  fi
}

if [ ! -e "$dir"/postgres_password ]; then
  password=$(random)
  (umask 077 && printf '%s' "$password" > "$dir"/postgres_password)
  # Hex only: nothing to escape in a connection string. Maximum Pool Size bounds the connections of the server.
  (umask 077 && printf 'Host=postgres;Port=5432;Database=elpaqueto;Username=elpaqueto;Password=%s;Maximum Pool Size=%s' \
    "$password" "$pool" > "$dir"/database_connection_string)
  give "$dir"/postgres_password 70 0400
  give "$dir"/database_connection_string 1654 0400
  echo "created "$dir"/postgres_password and "$dir"/database_connection_string"
fi

if [ ! -e "$dir"/admin_password.txt ]; then
  (umask 077 && printf '%s' "$(random)" > "$dir"/admin_password.txt)
  give "$dir"/admin_password.txt 1654 0400
  echo "created "$dir"/admin_password.txt (the administrator's password: keep it, then delete it after the bootstrap)"
fi
