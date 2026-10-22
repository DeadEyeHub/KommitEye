# Server and web refactor

KommitEye Service and Desktop are currently version 3.6.2. Both use the single
`Version` in `Directory.Build.props` and ship together in release `v<version>`.
The existing WPF application remains a compatibility host. Service is now the LAN
server plus the browser UI, not the WPF executable.

The web interface defaults to English. Use **Language / Язык** below the navigation
menu or on the sign-in screen to switch between English and Russian. The browser
remembers your selection; existing saved language preferences remain unchanged.

## Projects

| Project | Responsibility |
| --- | --- |
| Core | Shared settings, review results and server status records |
| Infrastructure | Git, model transport, reports, mail, credentials and persistence |
| Engine | ReviewRunner and headless ProjectCoordinator |
| Server | Authenticated ASP.NET Core API, SignalR, web hosting and service lifetime |
| Web | React/TypeScript client with relative `/api/v1` and `/hubs/events` URLs |
| KommitEye | Existing WPF compatibility host using the same extracted engine |

No shared library references WPF. Engine depends on Infrastructure and Core;
Infrastructure depends on Core and Platform. The server owns its workers independently of
requests or browser connections. Project commands are serialized; overlapping
worktrees/repository branches cannot run in separate projects simultaneously.

The server and shared libraries target `net10.0`. Windows retains DPAPI and its
existing Git authentication modes. Non-Windows hosts require a persistent key
certificate; see [platform architecture](platform-architecture.md). The published
MSI/ZIP packages remain Windows distributions.

Service mail settings use one `mail-settings.json` with `version`,
`activeProvider` and a `providers` object containing the saved SMTP/Gmail/Outlook
settings. On first load, the service migrates legacy settings, giving the main
file precedence over the active provider's older sidecar. It deletes the old
`.smtp`, `.gmail` and `.outlook` files only after the unified file is saved.
Encrypted passwords retain their existing protection. Desktop storage is unchanged;
do not share a data directory between the two hosts.

The Projects screen shows a live countdown when the server schedules its next
check. Creating a project starts with its name and requires an explicit branch
selection. Missing repository/branch selections produce validation errors before
a job is created. Clicking a report opens its formatted HTML in a new tab;
separate Markdown and HTML download links remain available.

## Run and build

Requirements: Windows, Git, .NET 10 SDK; Node 24 and pnpm 11.25.0 to build Web.

```powershell
dotnet run --project src/KommitEye.Server -- --urls http://0.0.0.0:5080
# Another terminal, during development:
cd src/KommitEye.Web
pnpm install --frozen-lockfile
pnpm exec vite
```

The Vite development proxy forwards API/events to localhost:5080. This development
address is not included in production JavaScript. The Service package serves both
the built UI and API itself. Server and Web are internal source projects; only
Service and Desktop are packaged for distribution.

```powershell
./build/publish.ps1                     # Service and Desktop
./build/publish.ps1 -Product Service       # full server + Web
./build/publish.ps1 -Product Desktop    # compatibility WPF executable
```

Output archives/executable and SHA256 lists are in `artifacts/`. Each invocation
uses a new staging directory so stale application files cannot enter the package.
Packages contain the .NET runtime. Git remains an external prerequisite.

## Running without service installation

Extract the Service ZIP into a permanent folder. From PowerShell in that folder:

```powershell
.\KommitEye.Server.exe
```

This starts the server interactively; no Windows service registration is needed.
Open `http://localhost:5080` and stop the server with Ctrl+C.
`Start-KommitEye.ps1` is an optional wrapper using the same configuration rules.
For LAN access, explicitly set `Urls` to `http://0.0.0.0:5080` and restart.

### Configuration initialization and location

Console and Windows service startup run the same initializer before starting HTTP:

- Windows default: `%ProgramData%\KommitEye\Server\server.json` (normally
  `C:\ProgramData\KommitEye\Server\server.json`).
- Linux/macOS default: `/etc/kommiteye/server.json`.
- `--config <absolute-path>` selects another location; it does not need to exist.
  `--config=<absolute-path>` is also supported. The working directory and service
  account do not change the default location.

```powershell
.\KommitEye.Server.exe --config "D:\KommitEye\config\server.json"
# Example for an ordinary account with its own writable configuration/data folder:
.\KommitEye.Server.exe --config "$env:LOCALAPPDATA\KommitEyeServer\server.json" --data-dir "$env:LOCALAPPDATA\KommitEyeServer\data"
```

A missing parent directory is created. A missing file is written to a temporary
file in the same directory, flushed, then renamed without replacing an existing
file. Concurrent starts cannot overwrite each other's configuration. Startup logs
show the absolute path and whether the file was **created** or **loaded**.
Existing files are preserved byte for byte, including invalid JSON. Invalid JSON,
unknown fields, invalid field values and access errors stop startup with the full
configuration path and a diagnostic. The initializer never changes ACLs or
inheritance; the running account must already have permission to create/read the
configuration and write the data directory. Choose a writable `--config` and
`--data-dir` if the system defaults are inaccessible. MSI, wrapper scripts and service commands also preserve existing permissions.
Administrators must arrange the service account's access before starting it.

### Supported settings

Only these four JSON fields are supported (names are case-insensitive):

| Field | Generated value | Effective default / meaning |
| --- | --- | --- |
| `DataDirectory` | Absolute default data path | `%ProgramData%\KommitEye\Server\data` on Windows or `/var/lib/kommiteye/server` on POSIX. A non-null value must be absolute; null/omitted uses `KOMMITEYE_DATA_DIR` first. |
| `Urls` | `"http://127.0.0.1:5080"` | Loopback access on port 5080. Null/omitted uses `ASPNETCORE_URLS` first. HTTP/HTTPS listen addresses separated by semicolons; no credentials, paths, queries or fragments. HTTPS also needs a configured certificate. |
| `GitExecutable` | `"git.exe"` (Windows) or `"git"` (POSIX) | This executable name enables PATH discovery. Alternatively, specify an absolute path to an existing executable with that name. |
| `CredentialProtection` | `"CurrentUser"` | Windows DPAPI under the running identity. `"LocalMachine"` is available only on Windows and is selected by new service installations. Changing it does not migrate existing encrypted secrets. |

The complete default file generated on Windows without initialization arguments is
shown below (the data path follows the actual ProgramData location). On POSIX,
the data path is `/var/lib/kommiteye/server` and the Git executable is `git`:

```json
{
  "DataDirectory": "C:\\ProgramData\\KommitEye\\Server\\data",
  "Urls": "http://127.0.0.1:5080",
  "GitExecutable": "git.exe",
  "CredentialProtection": "CurrentUser"
}
```

Existing files are preserved, including files generated by older versions.
`null` for the first three fields still means use environment/built-in defaults. Empty
strings are invalid. Omitting those fields has the same meaning as `null`;
omitting `CredentialProtection` selects `CurrentUser` (explicit `null` is invalid).
No passwords, API keys, certificate passwords or environment secrets are written.
Set the administrator password through the first-run web form; model/mail secrets
remain in the application's separate protected storage.

A complete customized Windows configuration:

```json
{
  "DataDirectory": "D:\\KommitEye\\data",
  "Urls": "http://127.0.0.1:5080",
  "GitExecutable": "C:\\Program Files\\Git\\cmd\\git.exe",
  "CredentialProtection": "CurrentUser"
}
```

### Precedence and restarts

Highest to lowest: command-line arguments, non-null file fields, environment
variables, built-in defaults. Arguments are `--data-dir`, `--urls`, `--git-path`,
`--credential-protection`; each accepts a separate value or `--name=value`.
`KOMMITEYE_DATA_DIR` and `ASPNETCORE_URLS` are the only environment fallbacks for
these JSON fields. Git discovery also uses PATH. Certificate settings
`KOMMITEYE_KEY_CERTIFICATE` and `KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE` remain
separate environment-only settings, required for non-Windows credential protection.

When creating a missing file, explicit CLI field values are saved as initial
settings; otherwise explicit built-in defaults are saved. Environment values are
never saved. The generated data path and URL therefore override environment
fallbacks; set those fields to `null` or omit them to use environment values. With an existing file, CLI values
only override the current process and do not edit the file. The same applies to
service SCM arguments. Review existing files before moving between accounts.

Every `server.json`, hosting CLI or hosting environment change requires a process
restart (Ctrl+C and relaunch, or `Restart-Service KommitEye`). Configuration is not
hot-reloaded. Changing the data directory does not copy existing data; changing
credential protection requires a separate migration of encrypted credentials.

MSI invokes `--initialize-config` with its chosen non-secret settings, then
`--install-service`. `Install-Service.ps1` delegates directly to `--install-service`. This uses exactly the same initializer but
exits after validation, without HTTP, data-store initialization or service
registration. Administrators may also use this flag to prepare/validate a file.
They do not implement a separate JSON writer. See
[Windows configuration and migration](windows-installer.md#service-configuration).

On first launch, open `http://localhost:5080` on the server computer and choose
your administrator password in the setup form. No password is generated. Any
nonempty password is accepted; very weak passwords show a nonblocking warning.
Initial setup is restricted to loopback connections; subsequent sign-in works
over the LAN. Set up before exposing a reverse proxy. An existing `admin.json`
preserves the current password and disables setup. An explicitly supplied
`KOMMITEYE_ADMIN_PASSWORD` can also initialize a new installation.
The login verifier is PBKDF2-hashed in `admin.json`; authentication
cookies are HttpOnly/SameSite and their signing keys are DPAPI protected.
Keep the data directory private. LAN HTTP is for trusted test networks; configure
TLS at a reverse proxy when transport encryption is required. No internet exposure,
firewall changes, service installation or real email sending is performed by builds.

## Settings and persistence

- Repository paths, SSH keys, Git executables and `localhost` model endpoints refer
  to the server computer, not the browser computer.
- Existing settings, cursors, report files, mail queues and protected OAuth stores
  retain their formats. SQLite `server.db` adds durable run history, idempotency keys
  and project settings revisions. It is not a migration of every existing store.
- One process exclusively locks the server data directory. Do not point Desktop
  and Server at the same directory. They are separate application hosts.
- Browser refresh/disconnect does not cancel work. Explicit Stop does. On server
  restart unfinished jobs become `interrupted`; they are not blindly replayed.
  Start monitoring again to resume the saved cursor. This avoids silent Git writes
  or duplicate analysis on startup.
- Project settings cannot be changed while running. Save conflicts return HTTP 409.
  Model and prompt forms keep a revision token and reject stale saves. Passwords
  and API keys are omitted from read responses; blank inputs preserve saved values.
- The SQLite run history and existing report/mail files are separate persistence
  boundaries. A crash between report save and mail enqueue can still miss an email;
  transactional outbox migration is a follow-up, not a guarantee of this release.
- SMTP testing is explicit. Failure notifications retain the 3.3.2 behavior: only
  configured recipients, without adding the author, independently of clean-review mail.

For migration, stop Desktop and Server, then run:

```powershell
./build/Import-DesktopData.ps1 -Source "$env:LOCALAPPDATA/KommitEye" -Destination 'D:/KommitEyeData'
```

The destination must be empty. Source files are never modified and remain a backup.
Copying preserves DPAPI blobs, which require the original Windows user/machine.
Reconnect mail after moving accounts or machines. Repository locations must exist
on the target server; copying settings does not copy repositories or SSH keys.

## Mail sign-in

SMTP works directly. Outlook uses Microsoft's device-code flow: configure a public
client application, enable public client flows, then provide `client ID;tenant ID`.
The browser shows the verification URL/code; token refresh and sends remain server-side.

Google supports two setups:

1. Desktop OAuth JSON: run the server interactively and complete the initial login
   in the browser on that server computer. Existing saved tokens subsequently work
   without an interactive session.
2. Web OAuth JSON: set `KOMMITEYE_GOOGLE_REDIRECT_URI` to the registered callback
   ending `/api/v1/oauth/google/callback`. It must use HTTPS, except localhost HTTP.
   The authorization link is displayed in the client, and callback state is checked
   once with a bounded lifetime. A bare LAN HTTP IP is not a valid Google Web OAuth
   deployment. Do not replace provider redirect validation with a permissive proxy.

`Install-Service.ps1` uses `NT SERVICE\KommitEye` without a password and invokes the server to create
`%ProgramData%\KommitEye\Server\server.json` if absent. It preserves all folder permissions and uses machine-scoped DPAPI for new service
configurations. Administrators must arrange program/config read access and data
modify access, including existing files. Grant repository access to that identity
and set its OAuth environment before starting the service. The ZIP script does
not start the service or add firewall rules; the MSI offers both options.

## Verification and GitHub

`checks/Server` starts a real HTTP process and isolated Git fixture. It verifies
authentication, command protection, settings conflicts, secret redaction, empty-diff
review, report access, commit paging, request deduplication and restart persistence.
It does not call real model, SMTP, Gmail or Outlook services.
Checks also cover sign-out without stopping work, explicit Stop and interrupted-job
recovery after process termination. The Service build job reruns the API checks against
the unpacked distribution, not only the development output.

```powershell
dotnet run --project checks/Server
dotnet run --project checks/ModelAvailability -- --deadlines
dotnet run --project checks/ReportRepair
```

Existing Gmail/Outlook, paging and WPF checks remain available. Browser QA covers
desktop and narrow layouts, login, connection status and the SMTP dialog.

GitHub Actions verifies changes on master, codex branches and pull requests. A
two-product matrix creates Service and Desktop artifacts. Only master
publishes an official version; all packages must succeed. A draft release receives
all artifacts/checksums before publication. Existing releases are not overwritten.
Local commits do not themselves run GitHub Actions until pushed. The refactored
projects now live at the repository root, and `.github/workflows/build.yml`
verifies and packages both Desktop and Service.

### Web repository connection and logs (3.4.1)

Project settings provide SSH Agent, SSH key file, PuTTY (.ppk), HTTPS and automatic
remote detection. Browse buttons select existing folders and files on the server;
they do not upload a client repository or transfer private key contents. Git
Credential Manager, SSH Agent and Pageant must be configured for the account
running the server (or its Windows service). Connection testing uses the same
Git access test as Desktop and does not fetch or advance the working branch.

Each project has a Logs button opening a separate dialog with journal and detailed
model/Git views, two-second refresh, optional auto-scroll, line filtering and a
download of the displayed tail. The tail is bounded to 500 entries / 128,000
characters. Clear requires confirmation and deletes only the selected log and its
rotated archive; it preserves reports, token counters and the other log type.

### HTML report export

The Reports page offers Markdown and HTML downloads for every saved report.
HTML is generated on demand from the current Markdown report, including old
reports, and contains its own styles for offline viewing and printing. Model
and Git text is HTML-escaped; no scripts or external assets are included.
Reports are persisted in `reports.db` in the data directory. Each repository/branch
has a group; each commit has one current review, with structured findings and its
Markdown text. Rechecking the same SHA replaces its result and findings, preserving
its position in the report. Markdown and HTML downloads are generated from SQLite.

On first use, existing `reports/*.md` files are imported automatically. Original
files remain untouched, and their complete text is also retained in `legacy_imports`.
Commit markers (including older manual-review markers) are recognized outside code
fences. Duplicate SHA entries use the last result at the first entry's position.
Legacy metadata that cannot be reliably recovered remains null; unknown-format
reports remain readable as their original text. Repository identity is attached
when a subsequent review matches the existing report filename.

Import is transactional per file and recorded by filename, so restarting does not
duplicate or resurrect cleared reports. Do not edit old Markdown files expecting
them to update SQLite. Clearing a report archives its database group; subsequent
reviews start a new group. Desktop opens a generated file in `report-exports/`.

Back up the data directory with the application/service stopped, including
`reports.db` and `reports.db-wal` if present. Do not copy only the database while
the application is running. `build/Import-DesktopData.ps1` copies both files and
requires exclusive access to them. Downgrading to a file-only version will not
show new SQLite reviews unless they have been exported.
