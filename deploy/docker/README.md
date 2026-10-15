# KommitEye Server in Docker

Linux container with the .NET server, React UI, Git and OpenSSH. Desktop remains
an independent Windows application. The application version comes from
`Directory.Build.props`; `kommiteye-server:local` is a local build tag, not a
published registry image. Linux amd64 is verified; ARM64 has not been tested.

## First start

Install Docker with Linux containers and the Compose v2 plugin. From the repository
root (PowerShell or a POSIX shell):

```sh
docker compose -f deploy/docker/compose.yaml build server
docker compose -f deploy/docker/compose.yaml run --rm init
docker compose -f deploy/docker/compose.yaml up -d --wait server
```

Open http://localhost:5080. Obtain the generated administrator password locally:

```sh
docker compose -f deploy/docker/compose.yaml exec -T server cat /run/secrets/admin_password
```

The command intentionally displays a secret: use your own terminal, not a public
CI log. The initializer generates a random password, a PFX certificate and its
password; no credentials are included in the image. You can instead create
`deploy/docker/local/secrets/admin-password` before initialization with your chosen
password on one line. Bootstrap only applies before `admin.json` exists; editing
this secret later does not reset an existing administrator password.

The initializer runs as root only to prepare the dedicated mounted directories.
The server runs as UID/GID **10001:10001**, with a read-only root filesystem and
no Linux capabilities. Initialization is repeatable and preserves the certificate
and existing data. If the certificate is missing while data exists, it refuses to
replace it: restore the original certificate from backup.

## Configuration and storage

| Host path under `deploy/docker/` | Container path | Purpose |
| --- | --- | --- |
| `server.json` | `/config/server.json` (read-only) | Data directory, listen URL, Git path |
| `local/data` | `/data` | Database, settings, reports, mail, key ring and Git home |
| `local/repos` | `/repos` | Repositories available to the server |
| `local/secrets/server.pfx` | `/run/secrets/key_certificate` | Protection certificate, not a TLS certificate |
| `local/secrets/certificate-password` | `/run/secrets/certificate_password` | PFX password |
| `local/secrets/admin-password` | `/run/secrets/admin_password` | First-start administrator password |

`local/` is ignored by Git and excluded from Docker builds. Compose secrets use
local files; they are not an encrypted vault. On Linux, the initializer restricts
file access to UID 10001 and root. On Windows, protect the host `local` folder with
Windows ACLs; Linux chmod on a Docker Desktop bind mount is not a substitute.

After editing `server.json`, restart the server. Paths inside JSON refer to the
container. Changing `/data` also requires changing the corresponding mount and
HOME directory. If you change port 5080 inside the container, update the Compose
port target and healthcheck as well. To change only the host port, set
`KOMMITEYE_PORT` before invoking Compose (for example, 5081).

The default host binding is **127.0.0.1**. For remote access, configure an HTTPS
reverse proxy and explicitly adjust bindings/networking. Model URLs also refer to
the container: `localhost` is the container itself. Docker Desktop exposes the host
as `host.docker.internal`; on Linux this can be added with a Compose
`extra_hosts: ["host.docker.internal:host-gateway"]` override.

## Repositories and authentication

Clone a repository into the writable mount, then use its container path in the UI:

```sh
docker compose -f deploy/docker/compose.yaml exec server git clone https://example.com/team/repo.git /repos/repo
```

Alternatively mount an existing dedicated checkout at `/repos/repo` using a Compose
override. Grant UID/GID 10001 the necessary access. Fetch/checkout operations require
write access. Avoid simultaneously modifying the same checkout from the host and
the service. For a trusted checkout owned by another UID, explicitly trust just
that path if Git reports dubious ownership:

```sh
docker compose -f deploy/docker/compose.yaml exec server git config --global --add safe.directory /repos/repo
```

Git's home is persisted at `/data/home`. For SSH, mount a dedicated key and verified
`known_hosts` into `/data/home/.ssh` (prefer read-only mounts), readable by UID 10001,
with private-key permissions 0600/0400 and directory permissions 0700. Use POSIX
SSH modes; Windows PuTTY/Pageant is unavailable. Do not disable host-key checking
or include private keys/tokens in the Dockerfile, build arguments or image. OAuth
callback URLs and provider registrations still need configuration as described in
`docs/server-web.md`; a container cannot open an interactive desktop browser.

Existing Windows DPAPI-encrypted SMTP/OAuth data cannot simply be copied to Linux.
Reconnect accounts/re-enter credentials when migrating. The container's certificate
and key ring must remain together for subsequent encrypted-data recovery.

## Update, backup and stop

Before upgrading, stop the server and back up **server.json, all of local/data,
local/secrets, and repositories not already backed up elsewhere**. On Linux use a
privileged backup tool preserving UID/GID and permissions. Keep the certificate
and its password with the backup in protected storage. Missing keys cannot be
recovered by rebuilding the image. Restore the complete backup while stopped;
retain UID/GID 10001 permissions and the original certificate.

After checking out the desired code version:

```sh
docker compose -f deploy/docker/compose.yaml build --pull server
docker compose -f deploy/docker/compose.yaml up -d --wait server
```

Rebuilding/recreating the container preserves bind-mounted data. `run --rm init`
may be repeated but is not required for ordinary updates. Do not regenerate the
certificate for an existing installation. A rollback should restore the matching
application version and data backup if storage formats changed.

```sh
docker compose -f deploy/docker/compose.yaml logs --tail 100 server
docker compose -f deploy/docker/compose.yaml stop server
docker compose -f deploy/docker/compose.yaml down
```

Stop forwards SIGTERM and allows 30 seconds for shutdown. `down` removes containers
and the Compose network; host data remains. Health status checks the HTTP server,
not external model, Git or mail provider availability.

## Verification

After building, run `pwsh -File build/test-docker.ps1` from the repository root.
It uses a unique Compose project, a temporary directory and an ephemeral loopback
port. It checks bootstrap, certificate preservation, non-root execution, Git,
React, persistent encrypted settings/cookies across replacement, graceful shutdown
and failures for missing protection material. It removes its own containers and
secret/data files afterwards. No real model or mail provider is contacted.

The reusable platform-checks workflow runs this on Ubuntu and already gates the
Windows package builds and release. This change does not publish an image to any
registry or change the shared application version.
