# Windows Service installer

`artifacts/KommitEye-Service-3.6.3-win-x64.msi` installs the server and its
compiled React UI for all users. The .NET 10 runtime is included. Git for Windows
must already be installed. Setup requires an administrator and .NET Framework
4.7.2 or newer, which is included in supported Windows 10/11 installations.

## Wizard

1. Choose the application directory (default: `C:\Program Files\KommitEye Server`).
2. Choose a separate local data directory (default: `C:\ProgramData\KommitEye\Server\data`),
   TCP port (default: 5080) and local-computer or LAN access.
3. For LAN access, optionally add Windows Firewall rules. They allow only the
   selected executable/port, on private and domain profiles, from the local subnet.
4. New installations use the Windows-managed identity `NT SERVICE\KommitEye` without
   a username/password prompt. Select the Git executable if auto-detection did not
   find it. Grant repository access to this identity and configure Git authentication
   for the service. Existing installations retain their original service account.
5. Choose automatic startup and whether to start immediately. Review the local
   and LAN addresses, then install.
6. Open `http://localhost:<port>` on the server computer to create the **web**
   administrator password. This is separate from the Windows account password.
   Configure repositories, model endpoints and mail in the browser.

The service is named `KommitEye` and displayed as **KommitEye Review Server**.
MSI, ZIP scripts and the executable **never change folder ACLs or inheritance**.
Existing program, configuration, data and repository permissions are preserved;
new folders inherit their parent's permissions. The administrator must provide
read/execute access to the program and Git, read access to configuration and
modify access to data, including existing files such as `server.lock` and SQLite
files. Repository access depends on the operations used. An old protected ACL
can prevent startup; setup reports the error rather than granting access.
Do not delete reports to work around an access error.

New service configurations use DPAPI `LocalMachine`. Administrators must keep
credential storage suitably restricted; setup no longer enforces this policy.
Existing encrypted credentials and their protection mode are not migrated.

## Executable service commands

Run from an elevated Administrator terminal, using an absolute EXE path:

```powershell
& "C:\Program Files\KommitEye Server\KommitEye.Server.exe" --install-service --config "C:\ProgramData\KommitEye\Server\server.json"
& "C:\Program Files\KommitEye Server\KommitEye.Server.exe" --start-service
& "C:\Program Files\KommitEye Server\KommitEye.Server.exe" --stop-service
& "C:\Program Files\KommitEye Server\KommitEye.Server.exe" --uninstall-service
```

Registration uses Windows SCM, defaults to automatic startup, and exits without
starting HTTP. Use `--service-start manual` to opt out of automatic startup.
New services use `NT SERVICE\KommitEye`, without a password. Administrators can
explicitly choose `--service-account` when creating a service; a regular account
also needs the Windows **Log on as a service** right and a password supplied using
`--service-password-stdin`. Passwords are never command-line arguments or JSON
settings. Registration refuses to change an existing account, preserving its
SCM credentials. A different executable/configuration requires `--update-service`.
Repeated registration with the same paths is supported; removing a missing service
succeeds. Removal stops the service first and never deletes configuration, data,
credentials or repositories. Errors return a nonzero exit code.

`Install-Service.ps1` and `Uninstall-Service.ps1` are thin wrappers around these
commands. They no longer implement SCM registration or permissions themselves.
An access failure includes a path in the Windows Application log; check the
service account's existing access rather than reinstalling or deleting data.

## Service configuration

The installer invokes `KommitEye.Server.exe --initialize-config` to create `%ProgramData%\KommitEye\Server\server.json` once:

```json
{
  "DataDirectory": "C:\\ProgramData\\KommitEye\\Server\\data",
  "Urls": "http://127.0.0.1:5080",
  "GitExecutable": "C:\\Program Files\\Git\\cmd\\git.exe",
  "CredentialProtection": "LocalMachine"
}
```

SCM starts the executable with `--config "<absolute path to server.json>"`.
Administrators must arrange configuration read access and data modify access for the service. Missing configuration is initialized by the executable using the same rules as
console startup; invalid/unreadable configuration or inaccessible data causes
startup failure. Initialization never changes folder ACLs.
Changes take effect after service restart. MSI maintenance accepts one HTTP URL
with an IPv4 host (optionally preceded by a loopback URL on the same port); it reads existing values and never overwrites the
file. For LAN access select it during first setup or change `Urls` and maintain the
firewall rules accordingly.

To move data, stop the service, back up and copy the entire data directory, grant
`NT SERVICE\KommitEye` full access to the new dedicated directory (administrators
and SYSTEM retain full access), update `DataDirectory`, then restart. Keep the
configuration outside the writable data directory. Do not change protection mode
on an existing store without migrating credentials. Machine-protected secrets
remain tied to their original Windows machine.

The MSI does not configure HTTPS. LAN HTTP is intended for a trusted network;
use a local reverse proxy for HTTPS, choosing local-computer access in setup.
Service accounts do not share the interactive user's Git credentials, SSH agents,
or DPAPI secrets. Configure authentication under the service account. Google
Desktop OAuth may require an interactive initial connection; see [server setup](server-web.md).

## Updates, repair and uninstall

Increase `Version` in `Directory.Build.props` for a new MSI release.
Use the new MSI to upgrade. The previous service is stopped and replaced inside
the Windows Installer transaction; installation failure rolls the upgrade back.
Setup reloads the previous folder, port, network options, Git path and account.
New virtual-account installations require no password during maintenance. Legacy
installations retain their data path, original account and `CurrentUser` protection;
re-enter that account password during maintenance. No automatic secret conversion
or move into the new default directory is performed. Existing server.json survives
repair, upgrade and uninstall; a failed installation can also leave this persistent
configuration for a retry.

Uninstall stops and removes the service, installed program files and firewall
rules. It preserves reports, settings, credentials and the data directory, plus
the non-secret setup settings at `HKLM\SOFTWARE\KommitEye\Server`, so reinstall
can reconnect to those data. The existing Windows user and its service-logon
right are preserved. Setup never removes repositories.

An existing service created by the old PowerShell script must first be removed
using that script, keeping its data. A fresh MSI installation requires an empty
data directory; data import is a separate, explicit operation.

## Build and verification

```powershell
./build/publish.ps1 -Product Service
```

This produces both the portable ZIP and the MSI plus a SHA256 checksum file.
WiX SDK, extensions and DTF are pinned to 6.0.2 through NuGet; no globally
installed WiX tool is needed. A Windows build host and the .NET SDK are required.

To rebuild just the MSI from an already published server directory:

```powershell
./build/publish-installer.ps1 -PayloadDirectory '<absolute path to published Service>'
dotnet run --project checks/Installer -c Release
./build/verify-installer.ps1 -Path artifacts/KommitEye-Service-3.6.3-win-x64.msi
```

The checks validate inputs, inspect the real MSI tables and invoke only the
read-only preflight actions in an MSI session, without installing a service.
WiX additionally runs MSI ICE validation during the build. The server
integration checks exercise the published payload separately. A full install,
repair, upgrade, rollback and uninstall smoke test should be performed in a
disposable Windows VM with a dedicated test account before production deployment.
These operations are not performed on the development computer by the build.

For unattended deployments, public MSI properties are `INSTALLFOLDER`, `DATADIR`,
`PORT`, `ACCESSMODE` (`lan` or `local`), `FIREWALL`, `AUTOSTART`, `STARTNOW` (`1` or
`0`), `CONFIGDIR` and `GITEXE`. `SERVICEACCOUNT` and `SERVICEPASSWORD` remain
available for legacy account maintenance; use protected deployment inputs for them. Removing the MSI needs no password.
Unattended installation runs the same validation as the wizard.

The local build is unsigned unless a signing step is configured separately.

### GitHub Actions

`Build and Release` builds the MSI alongside the portable server on pushes to
`master` and `codex/**`, pull requests targeting `master`, and manual runs.
The `KommitEye-Service` artifact includes the MSI, ZIP and SHA-256 checksums.
On `master`, a new `Version` in `Directory.Build.props` automatically publishes
one `v<version>` release with Desktop EXE, Service MSI, Service ZIP and combined
checksums, after both builds and checks pass. Both editions use the same version.
Existing releases are kept; bump the shared version if its tag is already published.

Service replaces the former Web Server distribution. The MSI upgrade code and
Windows service identity remain unchanged so existing Web Server 1.x installations
can upgrade to Service 3.6.3 while retaining their data and settings.

For a standalone server build, open **Actions → Build Service Installer → Run workflow**.
This manual workflow builds the web UI, self-contained server and WiX MSI, runs
installer and packaged-server checks, and uploads `KommitEye-Service-Installer`.
Download it from the run's **Artifacts** section. Artifacts are retained for 14 days.
The standalone workflow does not publish a GitHub release. Both workflows build
unsigned installers and do not perform a full service installation on the runner.

File-use detection uses Restart Manager (`MSIRESTARTMANAGERCONTROL=DisableShutdown`).
Do not set this to `Disable`: that selects the legacy file-use scanner, which can
spend minutes in `InstallValidate`. Executable commands stop the server inside the MSI transaction; Restart Manager does not automatically close other apps.
See https://learn.microsoft.com/en-us/windows/win32/msi/msirestartmanagercontrol.

MSI verbose logging is enabled for installation and removal. Windows places its
`MSI*.log` in the invoking account's temporary directory (or the system temporary
directory for system deployments). For a known uninstall log location, run:

```powershell
msiexec /x '<path to the installed version MSI>' /L*v "$env:TEMP\KommitEye-uninstall.log"
```

`build/measure-installer-validation.ps1 -Path <msi> -PayloadDirectory <published-server>`
times costing and file-use validation without installing or removing anything.
It is a diagnostic for the current file/process state, not a full uninstall benchmark.

### Startup and service identity (3.6.2)

On **Git and startup**, select **Start the server when Windows starts** to enable
Windows service automatic startup, without requiring a user to sign in. Clear it
for manual startup. **Start the server after installation** is a separate option.
Both options are available on new installation, upgrade and repair.

New installations and upgrades of `NT SERVICE\KommitEye` skip the account/password
screen. Only an actually installed service using a legacy Windows account shows
**Existing service account**. Registry settings left after uninstall no longer
cause the password screen to appear. Existing encrypted data is not automatically
migrated between service identities.

### Network access (3.6.3)

The separate **Network access** page offers local-only, all LAN interfaces, or
one IPv4 address selected from active adapters. The ready page displays a numeric
LAN URL, never the computer name. In all-interface mode the selected IP is the
example client URL; the server also listens on other interfaces. For a specific
IP, loopback remains enabled so initial administrator setup works locally.
Firewall rules remain limited to private/domain profiles and the local subnet.
Reserve the selected address in DHCP or use a static address. Changing an existing
server.json remains an explicit administrator action before MSI maintenance.

### Transaction and Windows verification

MSI stages a private copy of the new EXE/runtime below Program Files, using normal
inherited permissions. It snapshots SCM paths, account, startup mode and running
state, then stops the service before replacing files. The previous MSI is removed after
the first file-install script; registration/start runs in the second script after
old-product removal and configuration initialization. Failed installation removes a newly created
service; repair/upgrade rollback restores the old registration and running state
after file restoration. The snapshot contains no password. Legacy user-account
upgrades from older MSI releases may require the password shown in the wizard,
because those old packages delete the SCM registration during removal.
Uninstall stops the service inside the transaction but delays deletion until
commit, retaining SCM credentials if uninstall rolls back. Staged tools are then
removed. Upgrade-triggered removal by new packages leaves service ownership to
the incoming package. Data/configuration remain persistent, including on failure.

Run `build/test-windows-service.ps1 -Payload <published-directory> -Result <log>`
from an elevated terminal on Windows with no existing KommitEye service. It uses
a real virtual-account service and paths containing spaces, checks HTTP startup,
stop/removal, rollback, denied access and unchanged ACLs. It refuses to replace an
existing service. `build/test-service-script.ps1` runs this check on Windows CI.

`build/test-windows-installer.ps1 -Msi <msi> -Payload <published-directory> -Result <log>`
performs actual installation, repair, synthetic-version upgrade and uninstall,
including injected transaction failures. It compares ACLs/inheritance and checks
preserved configuration, a report, the reports database and repository contents.
Test MSI copies are modified only inside the fixture directory; the release version
is unchanged. Both packaging workflows run it on their disposable Windows runner.
