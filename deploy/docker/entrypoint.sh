#!/bin/sh
set -eu
# Bootstrap is only read for an empty data store by ServerSecurity.
if [ -n "${KOMMITEYE_ADMIN_PASSWORD_FILE:-}" ]; then
    KOMMITEYE_ADMIN_PASSWORD=$(cat "$KOMMITEYE_ADMIN_PASSWORD_FILE")
    [ -n "$KOMMITEYE_ADMIN_PASSWORD" ] || { echo "Administrator password file is empty." >&2; exit 1; }
    export KOMMITEYE_ADMIN_PASSWORD
fi
umask 077
mkdir -p "$HOME"
exec dotnet /app/KommitEye.Server.dll "$@"
