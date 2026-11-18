#!/usr/bin/env bash
# Creates the secret files of the PostgreSQL Compose sample in ./secrets (git-ignored), owner-only:
#   postgres_password            the password of the database user (read by PostgreSQL, UID 70)
#   database_connection_string   the Npgsql connection string, with the same password (read by ElPaqueto, UID 1654)
#   admin_password.txt           a password for the first administrator (read once by "admin bootstrap"; delete it after)
# Each file is checked on its own: a file that exists is never overwritten, a missing one is created, so an
# interrupted run can be repeated. Passwords are random and never printed. ADMIN_PASSWORD, when set, is used for
# admin_password.txt instead of a random one.
#
# Compose mounts a file secret as it is on the host (without Swarm, a secret's uid, gid and mode are ignored), so
# each file must belong to the user that reads it. The script uses chown, through sudo when it is not root; with
# CHOWN=0 it skips that and makes the files world-readable (a local test on a machine that nobody else uses).
#
# postgres_password is only read when PostgreSQL initializes an EMPTY data volume. Changing the file (or deleting it
# and running this script again) afterwards does not change the password of the existing database: change it in
# PostgreSQL with ALTER USER, then in both files.
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
  else
    # The mode first, while the file is still ours: after the chown only root can change it.
    chmod "$3" "$1"
    chown "$2:$2" "$1" 2> /dev/null || sudo chown "$2:$2" "$1"
  fi
}

if [ ! -e "$dir/postgres_password" ]; then
  rm -f "$dir/postgres_password.new"
  (umask 077 && random > "$dir/postgres_password.new")
  give "$dir/postgres_password.new" 70 0400
  mv "$dir/postgres_password.new" "$dir/postgres_password"
  echo "created $dir/postgres_password"
fi

if [ ! -e "$dir/database_connection_string" ]; then
  # The password is hex only: nothing to escape in a connection string. Maximum Pool Size bounds the connections
  # of the server. The password file may belong to UID 70: read it through sudo then.
  password=$(cat "$dir/postgres_password" 2> /dev/null || sudo cat "$dir/postgres_password")
  rm -f "$dir/database_connection_string.new"
  (umask 077 && printf 'Host=postgres;Port=5432;Database=elpaqueto;Username=elpaqueto;Password=%s;Maximum Pool Size=%s' \
    "$password" "$pool" > "$dir/database_connection_string.new")
  give "$dir/database_connection_string.new" 1654 0400
  mv "$dir/database_connection_string.new" "$dir/database_connection_string"
  echo "created $dir/database_connection_string"
fi

if [ ! -e "$dir/admin_password.txt" ]; then
  rm -f "$dir/admin_password.txt.new"
  (umask 077 && printf '%s' "${ADMIN_PASSWORD:-$(random)}" > "$dir/admin_password.txt.new")
  give "$dir/admin_password.txt.new" 1654 0400
  mv "$dir/admin_password.txt.new" "$dir/admin_password.txt"
  echo "created $dir/admin_password.txt (the first administrator's password: read it with sudo, use it for the bootstrap, then delete it)"
fi
