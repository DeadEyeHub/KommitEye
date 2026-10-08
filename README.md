# KommitEye

<img src="KommitEye/Assets/app-icon.png" alt="KommitEye logo" width="160" />

Windows desktop app for reviewing Git commits with a local or cloud
OpenAI-compatible model. Supports multiple projects, Markdown reports,
Gmail/Outlook notifications, and English/Russian interfaces.

## Requirements and setup

- Windows 10/11 and Git in `PATH`.
- A model endpoint supporting tool calls (for example vLLM, Ollama, LM Studio,
  or a compatible cloud API).
- .NET 10 SDK when building from source.

```powershell
dotnet run --project KommitEye/KommitEye.csproj
```

Select a repository and branch, configure and test a model profile, then click
**Start**. Projects can run concurrently; Start/Stop affect the selected project.
Closing the window keeps the app in the system tray. Use **Exit** to quit.

Build a self-contained Windows x64 executable:

```powershell
.\KommitEye\publish-win-x64.ps1
```

Output: `KommitEye/dist/KommitEye-<version>-win-x64.exe`. The .NET runtime is included.

## Reviewing commits

- Automatic monitoring tracks progress separately for each repository and branch.
- **Find commit** searches loaded hashes and descriptions; **Load 100 more**
  extends the list. Review any SHA manually or choose an automatic starting point.
- The model explores committed content using read-only Git tools. Reports contain
  findings with file and line locations; the Journal and Log show review progress.
- With fetch enabled, a checked-out local branch can advance by fast-forward
  after review. This requires a clean working copy. Select a remote-tracking
  branch to leave working files unchanged.
- Local-only repositories, linked worktrees, submodules, and private remotes
  are supported. Configure SSH/HTTPS authentication on the Project tab.

When the model is unavailable, the app keeps the commit pending and probes until
recovery. Configure the probe interval and response inactivity threshold in model
settings. At the review deadline, a silent model triggers availability checking;
recent responses instead produce an incomplete-review report. **Stop** cancels waiting.

**Apply changes** stays at the bottom while scrolling or switching tabs. It saves
edited settings and remains visible if validation fails. Checkboxes do not show
this bar. Model and prompt changes affect subsequent reviews, not an active request.

## Gmail notifications

1. Enable Gmail API in Google Cloud and configure the OAuth consent screen.
   In Testing mode, add your account to the test users.
2. Create a **Desktop app** OAuth client and download its JSON.
3. In **Mail**, select the JSON and connect Google through your browser.
4. Set recipients (separated by `;`), save, and enable notifications.
   **Send test email** checks delivery using a message without repository data.

The app requests `gmail.send`, `openid`, and `email`; it does not read your inbox.
Tokens refresh automatically and are stored with Windows DPAPI for the current
Windows user. Authorization errors pause delivery until reconnection. Disconnect
removes local authorization and attempts to revoke Google access.

Reports can be attached as HTML or Markdown. Sending clean reviews or sending to
commit authors is optional; enable author delivery only for trusted repositories.
Pending messages survive restarts and stay tied to their original account.
Provider acceptance does not guarantee inbox delivery, and retries after an
ambiguous network failure can produce duplicates.

Google Testing refresh tokens generally expire after seven days; see
[Google OAuth token expiration](https://developers.google.com/identity/protocols/oauth2#expiration).
Keep OAuth JSON files and token caches out of Git; `authMail/` and
`client_secret_*.json` are ignored.

## Outlook / Microsoft 365

The Mail section has separate Google and Outlook tabs; both connections and
settings are retained. Switching tabs does not change the sender. Use
**Save / use this account** to select the account for new reports.

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

Settings, review positions, reports, queues, and logs live in
`%LOCALAPPDATA%\KommitEye`. Set `KOMMITEYE_DATA_DIR` for an isolated data directory.
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
```
