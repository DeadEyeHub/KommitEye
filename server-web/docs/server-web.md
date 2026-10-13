# Server and web refactor

KommitEye Web Server starts at version 1.0.0. `Directory.Build.props` stores
`WebServerVersion` separately from the Desktop `Version` (3.4.2).
The existing WPF application remains a compatibility host. Web Server is now the LAN
server plus the browser UI, not the WPF executable.

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
Infrastructure depends on Core. The server owns its workers independently of
requests or browser connections. Project commands are serialized; overlapping
worktrees/repository branches cannot run in separate projects simultaneously.

The Windows target is intentional for the first release: DPAPI and existing Git
authentication modes are preserved. Linux is not advertised by these packages.

## Run and build

Requirements: Windows, Git, .NET 10 SDK; Node 24 and pnpm 11.25.0 to build Web.

```powershell
dotnet run --project src/KommitEye.Server -- --urls http://0.0.0.0:5080
# Another terminal, during development:
cd src/KommitEye.Web
pnpm install --frozen-lockfile
pnpm dev
```

The Vite development proxy forwards API/events to localhost:5080. This development
address is not included in production JavaScript. The Web Server package serves both
the built UI and API itself. Server and Web are internal source projects; only
Web Server and Desktop are packaged for distribution.

```powershell
./build/publish.ps1                     # Web Server and Desktop
./build/publish.ps1 -Product WebServer       # full server + Web
./build/publish.ps1 -Product Desktop    # compatibility WPF executable
```

Output archives/executable and SHA256 lists are in `artifacts/`. Each invocation
uses a new staging directory so stale application files cannot enter the package.
Packages contain the .NET runtime. Git remains an external prerequisite.

Extract Web Server, run `Start-KommitEye.ps1`, then open `http://<server-address>:5080`.
The startup script keeps data in the extracted package's `data` folder; supply
`-DataDirectory` to retain data independently of package updates. Direct server
launch defaults to `%LOCALAPPDATA%/KommitEye.Server`; override with
`KOMMITEYE_DATA_DIR` or `--data-dir <absolute-path>`.

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

`Install-Service.ps1` requires an explicit Windows account. Grant that account
repository/data access and set its OAuth environment before starting the service.
The installer does not start services or modify firewall rules automatically.

## Verification and GitHub

`checks/Server` starts a real HTTP process and isolated Git fixture. It verifies
authentication, command protection, settings conflicts, secret redaction, empty-diff
review, report access, commit paging, request deduplication and restart persistence.
It does not call real model, SMTP, Gmail or Outlook services.
Checks also cover sign-out without stopping work, explicit Stop and interrupted-job
recovery after process termination. The Web Server build job reruns the API checks against
the unpacked distribution, not only the development output.

```powershell
dotnet run --project checks/Server
dotnet run --project checks/ModelAvailability -- --deadlines
dotnet run --project checks/ReportRepair
```

Existing Gmail/Outlook, paging and WPF checks remain available. Browser QA covers
desktop and narrow layouts, login, connection status and the SMTP dialog.

GitHub Actions verifies changes on master, codex branches and pull requests. A
two-product matrix creates Web Server and Desktop artifacts. Only master
publishes an official version; all packages must succeed. A draft release receives
all artifacts/checksums before publication. Existing releases are not overwritten.
Local commits do not themselves run GitHub Actions until pushed. This standalone
copy is now under `server-web/`: its nested `.github/workflows` is preserved as a
reference and does not run automatically. The legacy root workflow is unchanged.

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
The Markdown source remains the persisted report used by the review engine.
