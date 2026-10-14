# Windows Service installer

`artifacts/KommitEye-Service-3.5.3-win-x64.msi` installs the server and its
compiled React UI for all users. The .NET 10 runtime is included. Git for Windows
must already be installed. Setup requires an administrator and .NET Framework
4.7.2 or newer, which is included in supported Windows 10/11 installations.

## Wizard

1. Choose the application directory (default: `C:\Program Files\KommitEye Server`).
2. Choose a separate local data directory (default: `C:\ProgramData\KommitEye.Server`),
   TCP port (default: 5080) and local-computer or LAN access.
3. For LAN access, optionally add Windows Firewall rules. They allow only the
   selected executable/port, on private and domain profiles, from the local subnet.
4. Enter an existing Windows account as `COMPUTER\user` or `DOMAIN\user` and its
   password, not a Windows Hello PIN. Select the Git executable if auto-detection
   did not find it. The account needs access to repositories and its own Git
   authentication. Setup grants "Log on as a service"; domain policy can override it.
5. Choose automatic startup and whether to start immediately. Review the local
   and LAN addresses, then install.
6. Open `http://localhost:<port>` on the server computer to create the **web**
   administrator password. This is separate from the Windows account password.
   Configure repositories, model endpoints and mail in the browser.

The service is named `KommitEye` and displayed as **KommitEye Review Server**.
The data folder's ACL grants full access only to SYSTEM, administrators and the
chosen account. The password is passed to Windows Service Control Manager, is a
hidden/secure MSI property and is not written to application configuration or
installer registry settings. Avoid supplying real passwords in shell history or
public deployment logs.

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
Re-enter the account password for upgrade or repair; it is not stored by setup.
Folder/account migration is deliberately separate because stored secrets use
Windows DPAPI and depend on the original account.

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
./build/verify-installer.ps1 -Path artifacts/KommitEye-Service-3.5.3-win-x64.msi
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
`0`), `SERVICEACCOUNT`, `SERVICEPASSWORD` and `GITEXE`. Supply credentials through
your deployment system's protected inputs. Removing the MSI needs no password.
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
can upgrade to Service 3.5.3 while retaining their data and settings.

For a standalone server build, open **Actions → Build Service Installer → Run workflow**.
This manual workflow builds the web UI, self-contained server and WiX MSI, runs
installer and packaged-server checks, and uploads `KommitEye-Service-Installer`.
Download it from the run's **Artifacts** section. Artifacts are retained for 14 days.
The standalone workflow does not publish a GitHub release. Both workflows build
unsigned installers and do not perform a full service installation on the runner.

File-use detection uses Restart Manager (`MSIRESTARTMANAGERCONTROL=DisableShutdown`).
Do not set this to `Disable`: that selects the legacy file-use scanner, which can
spend minutes in `InstallValidate`. ServiceControl stops and removes the server
inside the MSI transaction; Restart Manager does not automatically close other apps.
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
