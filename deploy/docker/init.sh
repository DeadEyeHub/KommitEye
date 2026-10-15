#!/bin/sh
set -eu
umask 077
mkdir -p /bootstrap/secrets /bootstrap/data/home /bootstrap/repos
# Never regenerate a certificate for an existing store: it protects saved secrets.
if [ ! -e /bootstrap/secrets/server.pfx ]; then
    if [ -n "$(find /bootstrap/data -type f -print -quit)" ]; then
        echo "Existing data without its certificate. Restore the original certificate; initialization refused." >&2
        exit 1
    fi
    [ -e /bootstrap/secrets/certificate-password ] || openssl rand -base64 32 > /bootstrap/secrets/certificate-password
    scratch=$(mktemp -d)
    trap 'rm -rf "$scratch"' EXIT HUP INT TERM
    openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 3650 -subj /CN=KommitEye-DataProtection \
        -keyout "$scratch/key.pem" -out "$scratch/cert.pem" 2>/dev/null
    openssl pkcs12 -export -inkey "$scratch/key.pem" -in "$scratch/cert.pem" \
        -out /bootstrap/secrets/server.pfx.tmp -passout file:/bootstrap/secrets/certificate-password
    mv /bootstrap/secrets/server.pfx.tmp /bootstrap/secrets/server.pfx
fi
# Validate the pair before reporting success, without printing secrets.
openssl pkcs12 -in /bootstrap/secrets/server.pfx -passin file:/bootstrap/secrets/certificate-password -noout
[ -e /bootstrap/secrets/admin-password ] || openssl rand -base64 24 > /bootstrap/secrets/admin-password
[ -s /bootstrap/secrets/admin-password ] || { echo "Administrator password file is empty." >&2; exit 1; }
chown 10001:10001 /bootstrap/data /bootstrap/data/home /bootstrap/repos /bootstrap/secrets /bootstrap/secrets/*
chmod 700 /bootstrap/data /bootstrap/data/home /bootstrap/repos
# Allow Compose on the host to stat secret paths without exposing their contents.
chmod 711 /bootstrap/secrets
chmod 400 /bootstrap/secrets/*
echo "Initialized. Read local/secrets/admin-password to sign in. Existing data and certificate were preserved."
