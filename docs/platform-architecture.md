# Platform architecture

The WPF desktop remains in `KommitEye/`, targeting `net10.0-windows`. It is an
independent application, not a client that requires the web server. A future
Avalonia application can consume the same engine without referencing WPF.
The desktop remains WPF; no Avalonia project is introduced. Docker packaging of
the portable server is described in `deploy/docker/README.md`.

## Dependencies

```text
Windows WPF Desktop ──┐
                     ├── Engine ── Infrastructure ── Core
ASP.NET Core Server ─┘                    │
                                         └── Platform ── Core
React Web ── HTTP/SignalR ── Server
```

- **Core** owns models and `ISecretProtector` / `IGitExecutables` contracts.
- **Platform** owns DPAPI and Windows/POSIX executable discovery. It has no WPF,
  ASP.NET Core, repository orchestration, or UI dependencies.
- **Infrastructure** owns Git operations, model transport, persistence, and mail.
  OAuth vaults and mail services accept a host-selected protector. Existing
  desktop callers keep the Windows CurrentUser default. On other OSes that
  default fails explicitly when credentials are used; it never writes plaintext.
- **Engine** owns review execution and project coordination.
- **Server** owns HTTP, authentication and the composition root in `Hosting/`.
  Its Data Protection adapter uses the server key ring. No separate Linux API
  implementation or build-time source patches are required.

All libraries and Server target `net10.0`. WPF and installer projects retain their
Windows targets. Non-UI fixture checks reference the engine rather than WPF.

## Credentials and compatibility

Windows without an explicit key certificate keeps the original DPAPI
`CurrentUser` format and null entropy for SMTP/OAuth, and DPAPI protection of
cookie keys in `session-keys/` by default. New Windows service installations
explicitly select `LocalMachine` through server.json and restrict data access to
SYSTEM, administrators and the service identity. Desktop and legacy services
keep `CurrentUser`; changing protection modes requires credential migration.

On non-Windows servers configure:

| Variable | Meaning |
| --- | --- |
| `KOMMITEYE_DATA_DIR` | Persistent directory for settings, SQLite, reports, queues and key ring |
| `KOMMITEYE_KEY_CERTIFICATE` | Path to a persistent PFX/PKCS#12 certificate containing its private key |
| `KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE` | Optional file containing its password; only trailing CR/LF is removed |
| `KOMMITEYE_ADMIN_PASSWORD` | Existing initial administrator bootstrap option; ignored once admin.json exists |

PFX loading uses ephemeral keys on Windows/Linux; macOS
requires a temporary on-disk keychain managed by .NET, so the platform loader uses
`DefaultKeySet` there. Hosts must dispose imported certificates when finished.
Provision the certificate outside the application. Keep it outside the data
directory and image, restrict private-key access to the service user, and retain
it across restarts and upgrades. Back up the certificate and key ring: losing
either can make saved credentials unreadable. Certificate rotation/migration is
not implemented in this change; do not replace the certificate casually.

The server refuses startup without the certificate on non-Windows systems.
ASP.NET Core encrypts key-ring files with the certificate; the key ring encrypts
SMTP/OAuth credentials and signs/encrypts authentication cookies. The credential
adapter uses a purpose separate from cookie authentication. Model configuration
API keys keep their existing storage behavior; this change does not encrypt all
configuration files.

An explicit certificate also selects this mode on Windows, allowing the same
backend to be tested there. Use a **new data directory** when changing protection
mode. DPAPI and certificate-backed stores are not interchangeable; reconnect mail
and re-enter SMTP credentials when migrating. No automatic conversion is attempted.

Desktop Linux/macOS key stores are future host implementations of
`ISecretProtector`; server certificates are not imposed on future desktop users.

## Git and host behavior

Windows retains its System32 OpenSSH agent and PuTTY discovery. POSIX uses `ssh`
from PATH and inherits `SSH_AUTH_SOCK`; PuTTY mode is rejected when used for a
remote operation. Private-key and known-host provisioning is the operator's
responsibility. Host paths are local to the running process, not the browser.
`--git-path` accepts `git.exe` on Windows and `git` on POSIX.

Windows service lifetime is registered only on Windows. One process owns one data
directory. Desktop and Server must not share that directory. UI browser/file
opening remains separate from headless review execution.

## Verification

```text
dotnet run --project checks/Platform/Platform.csproj -c Release
dotnet run --project checks/Server/Server.csproj -c Release
dotnet run --project checks/Server/Server.csproj -c Release -- --certificate-protection
dotnet run --project checks/CommitPaging/CommitPaging.csproj -c Release
dotnet run --project checks/ReportRepair/ReportRepair.csproj -c Release
dotnet run --project checks/Reports/Reports.csproj -c Release
dotnet run --project checks/ModelAvailability/ModelAvailability.csproj -c Release -- --deadlines
```

Server checks generate temporary certificates on non-Windows, or with the explicit
flag on Windows. They use isolated repositories and fake credentials, and do not
send mail or access real accounts. Windows Platform checks verify both directions
of legacy DPAPI compatibility. Existing Gmail/Outlook checks exercise Windows
defaults; the UI checks remain Windows-only.

`platform-checks.yml` is a reusable workflow running portable fixtures on Windows,
Linux and macOS. Build and Release calls it for the same commit and requires its
success before packaging or publishing a release. The manual installer workflow
also requires these checks. A failed, canceled, or skipped platform job blocks
downstream packaging and release; there is no `always()` bypass. The standalone
manual Platform checks run remains available for diagnostics.

Local `build/publish.ps1` builds Windows packages; it does not claim to validate
macOS/Linux. Cross-platform release gating is enforced by GitHub Actions.

Passing
server fixtures does not certify a future Avalonia UI or installer on those OSes.

## Docker deployment

`deploy/docker/` packages the existing portable server and React UI without changing
Desktop or shared review code. See [Docker operations](../deploy/docker/README.md)
for initialization, certificate-backed secrets, mounts, Git authentication and backup.
Docker integration checks are part of the required platform workflow.
