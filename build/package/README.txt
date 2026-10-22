KommitEye Service — LAN distribution

1. Extract the archive. Install Git for Windows if it is not in PATH.
2. Run KommitEye.Server.exe directly, or Start-KommitEye.ps1 in PowerShell. No service installation is required.
3. Open http://localhost:5080 on the server. For LAN access, set Urls to http://0.0.0.0:5080 and restart.
4. On first launch, open localhost on the server computer and choose your password.
   Weak passwords show a warning but are accepted. Existing passwords are preserved.
   Alternatively supply KOMMITEYE_ADMIN_PASSWORD before the FIRST launch.
5. Configure a repository path ON THE SERVER and a model endpoint reachable by it.

Service includes both the server and web interface in one distribution.
The frontend does not contain a hardcoded LAN address.

Data belongs to the running Windows account. Keep the data folder private to the
service administrator. HTTP is intended for a trusted test LAN; use an HTTPS reverse
proxy before sending secrets across an untrusted network. No firewall rules are
installed automatically. Allow your chosen port on the Private network profile.

Stop with Ctrl+C. Browser closure does not stop work. A server crash leaves running
jobs marked interrupted on the next launch; restart monitoring to resume the cursor.

Outlook: configure public client flows and enter client ID;tenant ID in Mail.
Google: Desktop OAuth requires a browser session on the server. For headless service
sign-in use a Web OAuth JSON and set KOMMITEYE_GOOGLE_REDIRECT_URI to the registered
HTTPS URL ending /api/v1/oauth/google/callback. Google does not allow arbitrary LAN
HTTP redirect addresses; localhost HTTP is an exception. Existing accounts refresh
tokens and send without a browser. SMTP works without OAuth.

Service installation is optional: run Install-Service.ps1 as administrator and
use the Windows-managed NT SERVICE\KommitEye identity without a user password.
The script delegates to EXE --install-service to create %ProgramData%\KommitEye\Server\server.json only if absent,
with DataDirectory defaulting to %ProgramData%\KommitEye\Server\data.
Edit that file as administrator and restart the service to apply changes.
No folder permissions or inheritance are changed. Administrators must ensure
the service can read the program/config and modify existing data files. Credentials use machine DPAPI;
keep the data ACL restricted. Existing interactive CurrentUser secrets require
separate migration; do not copy them into the new service unchanged.
Grant the service identity access to its repositories, then start the service.
Start-KommitEye.ps1 -ConfigPath <absolute-path> also supports config files;
without this option both EXE and launcher use the shared ProgramData configuration.
Uninstall-Service.ps1 preserves data. See the repository docs/server-web.md for tests,
migration and build instructions.

Direct startup creates missing server.json and parent directories automatically.
Default: %ProgramData%\KommitEye\Server\server.json (same for console/service).
Custom path: KommitEye.Server.exe --config "D:\KommitEye\server.json"
Default JSON: {"DataDirectory":null,"Urls":null,"GitExecutable":null,"CredentialProtection":"CurrentUser"}
Null DataDirectory uses KOMMITEYE_DATA_DIR, then %ProgramData%\KommitEye\Server\data.
Null Urls uses ASPNETCORE_URLS, then http://127.0.0.1:5080. Null GitExecutable uses PATH.
CLI --data-dir, --urls, --git-path and --credential-protection override file fields;
non-null file fields override environment variables, then built-in defaults apply.
Explicit CLI values are saved only when creating a missing file. Environment values
and secrets are never saved. Existing files are never overwritten; invalid JSON and
permission failures report the full path. Initialization does not modify folder ACLs.
All hosting configuration changes require a restart. --initialize-config prepares or
validates the file and exits without starting HTTP or registering a Windows service.
Complete documentation: docs/server-web.md in the source repository.

Executable service commands (elevated terminal):
  KommitEye.Server.exe --install-service --config "C:\ProgramData\KommitEye\Server\server.json"
  KommitEye.Server.exe --start-service
  KommitEye.Server.exe --stop-service
  KommitEye.Server.exe --uninstall-service
Registration defaults to automatic startup and does not start HTTP.
Use --service-start manual for manual startup. Existing accounts are preserved.

Manual access to existing reports (Russian): docs/service-permissions/README.md.
The ZIP includes the docs directory so the guide's related documentation is available offline.
