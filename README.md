# KommitEye

<img src="KommitEye/Assets/app-icon.png" alt="KommitEye logo" width="160" />

Review Git commits with a local or cloud OpenAI-compatible model. The repository
contains two applications sharing the same review engine:

| Application | Interface | Windows x64 distribution |
| --- | --- | --- |
| Desktop 3.5.3 | WPF desktop app with a system tray icon | Self-contained EXE |
| Service 3.5.3 | ASP.NET Core server with a bundled React browser UI | Windows service MSI or portable ZIP |

Both support multiple repositories, review reports and mail notifications.
Both editions share `Version` in `Directory.Build.props`. The server and React UI are
packaged together. Linux and Docker are not supported yet; the shared code still
uses Windows DPAPI for credentials.

## Requirements and setup

- Windows 10/11 and Git in `PATH`.
- A model endpoint supporting tool calls (for example vLLM, Ollama, LM Studio,
  or a compatible cloud API).
- Published packages include the .NET 10 runtime.
- Building from source requires the .NET 10 SDK. Building the web UI also requires
  Node.js 24 and pnpm 11.25.0, matching GitHub Actions.

### Service

Run `KommitEye-Service-<version>-win-x64.msi` as an administrator. Select the
application/data folders, port, network access, an existing Windows service
account and its password, and the Git executable. Setup requires .NET Framework
4.7.2 or newer for installer actions, in addition to the bundled server runtime.

Alternatively, extract the portable ZIP and run `Start-KommitEye.ps1`.
On the server computer, open `http://localhost:5080` (or your selected port) and
create the web administrator password. The Windows service password and web
administrator password are separate. Repository paths and Git credentials refer
to the server computer, not the browser computer.

The MSI installs the `KommitEye` service. Uninstall through Windows Installed apps
or the MSI's **Remove** option. Application files, the service and installer-created
firewall rules are removed; data and saved installation settings are retained.

See [server configuration and migration](docs/server-web.md) and
[MSI setup, upgrades and removal](docs/windows-installer.md).

### Desktop

Run the published `KommitEye-Desktop-<version>-win-x64.exe`, or start from source:

```powershell
dotnet run --project KommitEye/KommitEye.csproj
```

Select a repository and branch, configure and test a model profile, then click
**Start**. Projects can run concurrently; Start/Stop affect the selected project.
Closing the window keeps the app in the system tray. Use **Exit** to quit.

## Build packages

Run from the repository root:

```powershell
./build/publish.ps1 -Product Desktop
./build/publish.ps1 -Product Service
./build/publish.ps1                     # both products
```

Outputs in `artifacts/`:

- `KommitEye-Desktop-3.5.3-win-x64.exe`
- `KommitEye-Service-3.5.3-win-x64.zip`
- `KommitEye-Service-3.5.3-win-x64.msi`
- `SHA256SUMS-<product>.txt`

WiX is restored through NuGet; a global WiX installation is not needed. MSI builds
run package and read-only preflight checks, without installing the service.
Local installers are unsigned. The desktop-only helper
`./KommitEye/publish-win-x64.ps1` also places its EXE in `KommitEye/dist/`.

### GitHub Actions

- **Build and Release** verifies the code and builds both Desktop and Service
  on pushes to `master`/`codex/**`, pull requests to `master`, and manual runs.
  Download `KommitEye-Desktop` and `KommitEye-Service` from the run's **Artifacts**.
  Successful push/manual runs on `master` publish one `KommitEye <version>` release
  with tag `v<version>`, containing Desktop EXE, Service MSI, Service ZIP and a
  combined SHA-256 list. Publication waits for both packages and all checks.
  Bump `Version` and push to `master` for the next complete release.
  Existing releases are left unchanged; use a new version if its tag is already published.
- **Build Service Installer** is a manual server-only build: **Actions → Build Service
  Installer → Run workflow**. Download `KommitEye-Service-Installer` for the MSI,
  portable ZIP and checksums. This workflow does not publish a release.

Workflow artifacts are retained for 14 days. Build outputs are not committed to Git.

## Repository layout

| Path | Contents |
| --- | --- |
| `KommitEye/` | WPF desktop UI, assets and default configuration templates |
| `src/KommitEye.Core/` | Shared models |
| `src/KommitEye.Infrastructure/` | Git, model API, mail and persistence |
| `src/KommitEye.Engine/` | Review execution and project coordination |
| `src/KommitEye.Server/` | API, authentication, background work and web hosting |
| `src/KommitEye.Web/` | React/TypeScript browser UI |
| `build/` | Publishing, migration, service scripts and WiX installer sources |
| `checks/` | Automated fixture and integration checks |
| `docs/` | Deployment and installer documentation |
| `.github/workflows/` | CI builds and release automation |

`artifacts/`, `bin/`, `obj/`, `dist/`, `node_modules/`, local data and `authMail/`
are ignored. Keep local secrets out of source files; `.gitignore` does not remove
files already committed to Git. Commit example configuration and the pnpm lockfile,
not real credentials or generated packages.

## Reviewing commits

The window/button instructions below describe Desktop. For the browser interface
and its mail authentication flows, see [Service documentation](docs/server-web.md).

- Automatic monitoring tracks progress separately for each repository and branch.
- **Find commit** searches loaded hashes and descriptions; **Load 100 more**
  extends the list. Review any SHA manually or choose an automatic starting point.
- The model explores committed content using read-only Git tools. Reports contain
  findings with file and line locations; the Journal and Log show review progress.
- With fetch enabled, a checked-out local branch can advance by fast-forward
  after review. This requires a clean working copy. Select a remote-tracking
  branch to leave working files unchanged.
- Local-only repositories, linked worktrees, submodules, and private remotes
  are supported. Configure SSH/HTTPS authentication in **Project**.

When the model is unavailable, the app keeps the commit pending and probes until
recovery. Configure the probe interval and response inactivity threshold in model
settings. At the review deadline, a silent model triggers availability checking;
recent responses instead produce an incomplete-review report. **Stop** cancels waiting.

The main window shows the project dashboard. Five buttons below **Recent journal entries** open Project, Models,
Mail, System prompt, and Journal in separate windows. **Apply changes** saves and
keeps the window open; **OK** saves and closes it. **Cancel** or closing the window
discards edits since the last Apply, including checkbox changes. Model and prompt
changes affect subsequent reviews, not an active request.

## Gmail notifications

1. Enable Gmail API in Google Cloud and configure the OAuth consent screen.
   In Testing mode, add your account to the test users.
2. Create a **Desktop app** OAuth client and download its JSON.
3. In **Mail**, select the JSON and click Connect, then **Apply changes**
   to authorize Google through your browser.
4. Set recipients (separated by `;`), enable notifications, and apply changes.
   **Send test email** checks delivery using a message without repository data.

The app requests `gmail.send`, `openid`, and `email`; it does not read your inbox.
Tokens refresh automatically and are stored with Windows DPAPI for the current
Windows user. Authorization errors pause delivery until reconnection. Disconnect
removes local authorization and attempts to revoke Google access.

Reports can be attached as HTML or Markdown. Sending clean reviews or sending to
commit authors is optional; enable author delivery only for trusted repositories.
Terminal review failures are sent to the configured **Recipients**, without adding
the commit author, even when clean-review notifications are disabled. Notifications
must be enabled. Canceled reviews and temporary model outages do not send failure mail.
Pending messages survive restarts and stay tied to their original account.
Provider acceptance does not guarantee inbox delivery, and retries after an
ambiguous network failure can produce duplicates.

Google Testing refresh tokens generally expire after seven days; see
[Google OAuth token expiration](https://developers.google.com/identity/protocols/oauth2#expiration).
Keep OAuth JSON files and token caches out of Git; `authMail/` and
`client_secret_*.json` are ignored.

## Outlook / Microsoft 365

The Mail section has separate Google, Outlook, and SMTP tabs; connections and
settings are retained. Switching tabs does not change the sender. Use
**Use this account**, then **Apply changes**, to select the sender for new reports.
Connecting and disconnecting accounts also take effect only after Apply or OK.

For SMTP, open **Mail → SMTP → SMTP server settings…**. The separate dialog contains
the host, port, security mode, username, password, and sender address. Port 587 with
StartTLS is the default; SSL-on-connect and unencrypted connections are also available.
A blank password keeps the saved password; use **Clear saved password** to remove it.
Passwords are protected with Windows DPAPI. Confirm the dialog, set recipients and
notification preferences in Mail, and click **Apply changes** or **OK** to save.
Use **Send test email** to test the current settings without saving them.

Register a Microsoft Entra application for your organization or for personal and work/school accounts.
Add the **Mobile and desktop applications** platform with `http://localhost`
as its redirect URI and Microsoft Graph delegated permissions `User.Read` and
`Mail.Send`. Enter **Application (client) ID** and **Directory (tenant) ID** from Overview in the Outlook tab (use `common` for personal/multitenant apps), then connect
through your browser. No client secret is required; organization policies may
require administrator consent.

Tokens refresh automatically and the MSAL cache is protected with Windows DPAPI.
Disconnect removes local tokens; it does not revoke Microsoft consent or sign out
the browser. Queued messages stay tied to their original provider and account.

## Data and compatibility

Desktop settings, review positions, reports, queues, and logs live in
`%LOCALAPPDATA%\KommitEye`. Set `KOMMITEYE_DATA_DIR` for an isolated data directory.
Service uses `%PROGRAMDATA%\KommitEye.Server` by default when installed by MSI,
the extracted package's `data/` folder with the portable startup script, or
`%LOCALAPPDATA%\KommitEye.Server` when launched directly. Do not share a data
directory between Desktop and Server.
Model profiles may contain API keys; detailed logs can contain repository content.
Inspect logs and crash reports before sharing them.

Previously called **Git Reviewer**. Existing installations keep using
`%LOCALAPPDATA%\GitReviewer` if the new directory does not exist.
`GITREVIEWER_DATA_DIR` and `GITREVIEWER_SNAPSHOT_MB` remain supported; the new
`KOMMITEYE_*` names take precedence. Existing data is not deleted by the rename.

## Checks

Run from the repository root on Windows; these use fixtures instead of real
model or email accounts:

```powershell
dotnet build KommitEye/KommitEye.csproj
dotnet run --project checks/Gmail/Gmail.csproj
dotnet run --project checks/Outlook/Outlook.csproj
dotnet run --project checks/ApplyChanges/ApplyChanges.csproj
dotnet run --project checks/CommitPaging/CommitPaging.csproj
dotnet run --project checks/ReportRepair/ReportRepair.csproj
dotnet run --project checks/ModelAvailability/ModelAvailability.csproj
dotnet run --project checks/Server/Server.csproj
dotnet run --project checks/Installer/Installer.csproj
```
