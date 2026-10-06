#!/bin/sh
# Container entrypoint.
#
# /app/data is exposed as a volume. When it is backed by a host bind mount (rather
# than a Docker-managed named volume) its ownership comes from the host and is not
# necessarily appuser, so the runtime cannot create the nested vault directory it
# needs. Fix the ownership up as root, once, before dropping to the app user.
set -e

DATA_DIR="${FOREXBOT_DATA_DIR:-/app/data}"

if [ -d "$DATA_DIR" ]; then
    mkdir -p "$DATA_DIR/vault" 2>/dev/null || true
    chown -R appuser:appuser "$DATA_DIR" 2>/dev/null || true
fi

# Drop privileges and hand off to the application. `exec` keeps signal handling
# (docker stop / SIGTERM) working against the .NET process itself.
exec runuser -u appuser -- dotnet WebAPI.dll "$@"
