KommitEye Service — LAN distribution

1. Extract the archive. Install Git for Windows if it is not in PATH.
2. Run Start-KommitEye.ps1 in PowerShell.
3. Open http://localhost:5080 on the server or http://<server-address>:5080 in your LAN.
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
The script creates %ProgramData%\KommitEye\Server\server.json only if absent,
with DataDirectory defaulting to %ProgramData%\KommitEye\Server\data.
Edit that file as administrator and restart the service to apply changes.
The service can read the config and write data. Credentials use machine DPAPI;
keep the data ACL restricted. Existing interactive CurrentUser secrets require
separate migration; do not copy them into the new service unchanged.
Grant the service identity access to its repositories, then start the service.
Start-KommitEye.ps1 -ConfigPath <absolute-path> also supports config files;
without this option the portable launcher retains its local data.
Uninstall-Service.ps1 preserves data. See the repository docs/server-web.md for tests,
migration and build instructions.
