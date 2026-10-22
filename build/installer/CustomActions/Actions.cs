using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace KommitEye.Setup;

public static class Actions
{
    private const string RegistryPath = @"SOFTWARE\KommitEye\Server";
    private static RegistryKey Saved() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(RegistryPath);

    [CustomAction]
    public static ActionResult InitializeSettings(Session session)
    {
        if (session["SETUP_INITIALIZED"] == "1") return ActionResult.Success;
        try
        {
            using (var saved = Saved())
            {
                void Default(string property, string fallback) { if (session[property] == "") session[property] = saved?.GetValue(property) as string ?? fallback; }
                Default("INSTALLFOLDER", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KommitEye Server"));
                Default("CONFIGDIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server"));
                Default("DATADIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server", "data"));
                Default("BINDIP", "");
                Default("PORT", "5080"); Default("ACCESSMODE", "local"); Default("FIREWALL", "0");
                var installedAccount = InstalledServiceAccount();
                session["SERVICEACCOUNT"] = SetupValidation.InstallationAccount(installedAccount);
                session["LEGACYSERVICE"] = SetupValidation.IsLegacyService(installedAccount) ? "1" : "";
                if (session["LEGACYSERVICE"] != "1") session["SERVICEPASSWORD"] = "";
                if (session["AUTOSTART"] == "")
                {
                    using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\KommitEye"))
                        session["AUTOSTART"] = installedAccount == null || (int?)service?.GetValue("Start") == 2 ? "1" : "0";
                }
                Default("STARTNOW", "1");
                Default("GITEXE", FindGit());
                var configPath = Path.Combine(session["CONFIGDIR"], "server.json");
                if (File.Exists(configPath))
                {
                    var config = ServiceConfiguration.Read(configPath);
                    session["DATADIR"] = config.DataDirectory;
                    session["GITEXE"] = config.GitExecutable;
                    var addresses = (config.Urls ?? "http://127.0.0.1:5080").Split(';');
                    var endpoint = addresses.Last();
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) || address.Scheme != "http" ||
                        !IPAddress.TryParse(address.Host, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork ||
                        addresses.Length > 2 || (addresses.Length == 2 && addresses[0] != "http://127.0.0.1:" + address.Port))
                        throw new ArgumentException("MSI maintenance requires HTTP IPv4 addresses, optionally preceded by the loopback address on the same port.");
                    session["PORT"] = address.Port.ToString();
                    session["ACCESSMODE"] = address.Host == "127.0.0.1" ? "local" : address.Host == "0.0.0.0" ? "lan" : "ip";
                    if (session["ACCESSMODE"] == "ip") session["BINDIP"] = address.Host;
                }
            }
            PopulateAddresses(session);
            session["AUTOCHOICE"] = session["AUTOSTART"] == "1" ? "1" : "";
            session["STARTCHOICE"] = session["STARTNOW"] == "1" ? "1" : "";
            session["FIREWALLCHOICE"] = session["FIREWALL"] == "1" ? "1" : "";
            session["NETWORKCHOICE"] = session["ACCESSMODE"];
            session["SETUP_INITIALIZED"] = "1";
            return ActionResult.Success;
        }
        catch { return Error(session, "Unable to read previous installation settings."); }
    }

    private static string[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
        .Select(a => a.ToString()).Distinct().OrderBy(a => a).ToArray();

    private static void PopulateAddresses(Session session)
    {
        var addresses = LocalAddresses().ToList();
        if (session["BINDIP"] != "" && !addresses.Contains(session["BINDIP"])) addresses.Add(session["BINDIP"]);
        if (session["BINDIP"] == "" && addresses.Count > 0) session["BINDIP"] = addresses[0];
        using (var view = session.Database.OpenView("SELECT `Property`, `Order`, `Value`, `Text` FROM `ComboBox`"))
        {
            view.Execute();
            for (var i = 0; i < addresses.Count; i++)
            {
                using (var row = new Record(4))
                {
                    row.SetString(1, "BINDIP"); row.SetInteger(2, i + 100);
                    row.SetString(3, addresses[i]); row.SetString(4, addresses[i]);
                    view.Modify(ViewModifyMode.InsertTemporary, row);
                }
            }
        }
    }

    private static string ConfigureNetwork(Session session)
    {
        var port = SetupValidation.Port(session["PORT"]);
        var addresses = LocalAddresses();
        if (session["ACCESSMODE"] == "lan" && !addresses.Contains(session["BINDIP"]))
            session["BINDIP"] = addresses.FirstOrDefault() ?? "";
        var urls = SetupValidation.ListenUrls(session["ACCESSMODE"], session["BINDIP"], port, addresses);
        session["LISTENHOST"] = session["ACCESSMODE"] == "local" ? "127.0.0.1" : session["ACCESSMODE"] == "lan" ? "0.0.0.0" : session["BINDIP"];
        session["LANADDRESS"] = session["ACCESSMODE"] == "local" ? "LAN access is disabled." :
            addresses.Contains(session["BINDIP"]) ? "LAN address: http://" + session["BINDIP"] + ":" + port :
            "No active LAN IPv4 address. Connect a network adapter before using LAN access.";
        return urls;
    }

    [CustomAction]
    public static ActionResult ValidateNetwork(Session session)
    {
        session["NETWORKVALID"] = "";
        session["ACCESSMODE"] = session["NETWORKCHOICE"];
        try { ConfigureNetwork(session); session["NETWORKVALID"] = "1"; }
        catch (ArgumentException error) { Error(session, error.Message); }
        return ActionResult.Success;
    }

    private static string InstalledServiceAccount()
    {
        using (var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\KommitEye"))
        {
            if (service == null) return null;
            return service.GetValue("ObjectName") as string
                ?? throw new ArgumentException("Cannot determine the installed KommitEye service identity.");
        }
    }

    private static string FindGit()
    {
        using (var git = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\GitForWindows"))
        {
            var root = git?.GetValue("InstallPath") as string;
            if (root != null && File.Exists(Path.Combine(root, "cmd", "git.exe"))) return Path.Combine(root, "cmd", "git.exe");
        }
        var paths = (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';');
        return paths.Select(p => Path.Combine(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')), "git.exe")).FirstOrDefault(File.Exists) ?? "";
    }

    [CustomAction]
    public static ActionResult ValidateDialog(Session session)
    {
        session["ACCESSMODE"] = session["NETWORKCHOICE"];
        session["AUTOSTART"] = session["AUTOCHOICE"] == "1" ? "1" : "0";
        session["STARTNOW"] = session["STARTCHOICE"] == "1" ? "1" : "0";
        session["FIREWALL"] = session["FIREWALLCHOICE"] == "1" ? "1" : "0";
        session["SETUPVALID"] = "";
        try { Validate(session); session["SETUPVALID"] = "1"; }
        catch (ArgumentException error) { Error(session, error.Message); }
        catch { Error(session, "Cannot validate the settings. Check local folders, the Windows account and Git installation."); }
        return ActionResult.Success;
    }

    [CustomAction]
    public static ActionResult ValidateExecute(Session session)
    {
        try { Validate(session); return ActionResult.Success; }
        catch (ArgumentException error) { return Error(session, error.Message); }
        catch { return Error(session, "Cannot validate server installation settings."); }
    }

    private static void Validate(Session session)
    {
        var install = SetupValidation.DirectoryPath(session["INSTALLFOLDER"]);
        var data = SetupValidation.DirectoryPath(session["DATADIR"]);
        var configDirectory = SetupValidation.DirectoryPath(session["CONFIGDIR"]);
        var configPath = Path.Combine(configDirectory, "server.json");
        if (configDirectory.Equals(data, StringComparison.OrdinalIgnoreCase) || configDirectory.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The configuration folder cannot be inside the writable data folder.");
        if (SetupValidation.Overlaps(install, configDirectory)) throw new ArgumentException("The configuration folder must be separate from the program folder.");
        if (SetupValidation.Overlaps(install, data)) throw new ArgumentException("The program and data folders must be separate and cannot contain each other.");
        if (SetupValidation.Overlaps(data, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new ArgumentException("The data folder cannot be inside the Windows folder.");
        var port = SetupValidation.Port(session["PORT"]);
        var urls = ConfigureNetwork(session);
        foreach (var property in new[] { "AUTOSTART", "STARTNOW", "FIREWALL" })
            if (session[property] != "0" && session[property] != "1" && session[property] != "") throw new ArgumentException("Invalid installation option: " + property);
        var account = SetupValidation.Account(session["SERVICEACCOUNT"]);
        var fullAccount = account[0] + "\\" + account[1];
        var virtualAccount = fullAccount.Equals(SetupValidation.VirtualServiceAccount, StringComparison.OrdinalIgnoreCase);
        var sid = virtualAccount ? new SecurityIdentifier(SetupValidation.VirtualServiceSid) : (SecurityIdentifier)new NTAccount(fullAccount).Translate(typeof(SecurityIdentifier));
        if (virtualAccount) session["SERVICEPASSWORD"] = "";
        if (!virtualAccount && session["SERVICEPASSWORD"].Length == 0) throw new ArgumentException("Enter the Windows account password. A Windows Hello PIN cannot be used for a service.");
        using (var saved = Saved())
        {
            var previousData = saved?.GetValue("DATADIR") as string;
            var previousAccount = InstalledServiceAccount();
            if (!File.Exists(configPath) && previousData != null && !string.Equals(previousData.TrimEnd('\\'), data, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the existing data folder during upgrade or repair. Data migration must be performed separately.");
            if (previousAccount != null && !string.Equals(previousAccount, fullAccount, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the existing service account during upgrade or repair so encrypted credentials remain readable.");
            if (saved == null && !File.Exists(configPath) && Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
                throw new ArgumentException("Choose an empty data folder for the first installation. Import existing data separately.");
            var service = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals("KommitEye", StringComparison.OrdinalIgnoreCase));
            using (service)
            {
                if (service != null && saved == null) throw new ArgumentException("A KommitEye service already exists outside this installer. Remove that service first, keeping its data.");
                var ownPort = service != null && service.Status == ServiceControllerStatus.Running && saved?.GetValue("PORT") as string == session["PORT"];
                if (!ownPort)
                {
                    var listeners = (session["ACCESSMODE"] == "ip"
                        ? new[] { IPAddress.Loopback, IPAddress.Parse(session["LISTENHOST"]) }
                        : new[] { IPAddress.Parse(session["LISTENHOST"]) })
                        .Select(ip => new TcpListener(ip, port)).ToArray();
                    try { foreach (var listener in listeners) { listener.Server.ExclusiveAddressUse = true; listener.Start(); } }
                    catch (SocketException) { throw new ArgumentException("The selected TCP port is already in use or unavailable."); }
                    finally { foreach (var listener in listeners) listener.Stop(); }
                }
            }
            session["RollbackStartMode"] = new CustomActionData { { "mode", saved == null || saved.GetValue("AUTOSTART") as string == "1" ? "2" : "3" } }.ToString();
        }
        var gitExe = session["GITEXE"];
        if (string.IsNullOrWhiteSpace(gitExe) || !Path.IsPathRooted(gitExe) || gitExe.IndexOf('"') >= 0 || !File.Exists(gitExe) || !Path.GetFileName(gitExe).Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Install Git for Windows, then select its git.exe (usually C:\\Program Files\\Git\\cmd\\git.exe).");
        var gitStart = new ProcessStartInfo(gitExe, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var git = Process.Start(gitStart))
        {
            if (!git.WaitForExit(5000)) { git.Kill(); throw new ArgumentException("Git did not respond within five seconds."); }
            if (git.ExitCode != 0) throw new ArgumentException("Git could not run. Repair the Git for Windows installation.");
        }
        if (File.Exists(configPath))
        {
            var config = ServiceConfiguration.Read(configPath);
            var expectedUrl = urls;
            if (!string.Equals(config.DataDirectory.TrimEnd('\\'), data, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(config.GitExecutable, gitExe, StringComparison.OrdinalIgnoreCase) || config.Urls != expectedUrl ||
                config.CredentialProtection != (virtualAccount ? "LocalMachine" : "CurrentUser"))
                throw new ArgumentException("Existing server.json is preserved. Change DataDirectory, Urls and GitExecutable in that file before running maintenance.");
        }
        session["CONFIGDIR"] = configDirectory + "\\";
        session["CONFIGFILE"] = configPath;
        session["WriteServiceConfig"] = new CustomActionData { { "exe", Path.Combine(install, "KommitEye.Server.exe") }, { "path", configPath }, { "data", data },
            { "urls", urls },
            { "git", gitExe }, { "protection", virtualAccount ? "LocalMachine" : "CurrentUser" } }.ToString();
        session["DATADIR"] = data + "\\";
        session["DATAPATH"] = data;
        session["SERVICEACCOUNT"] = fullAccount; session["SERVICEUSER"] = account[1]; session["SERVICEDOMAIN"] = account[0]; session["SERVICESID"] = sid.Value;
        session["ConfigureStartMode"] = new CustomActionData { { "mode", session["AUTOSTART"] == "1" ? "2" : "3" } }.ToString();
    }

    private static ActionResult Error(Session session, string message)
    {
        using (var record = new Record(1)) { record.FormatString = "[1]"; record[1] = message; session.Message(InstallMessage.Error, record); }
        return ActionResult.Failure;
    }

    [CustomAction]
    public static ActionResult ConfigureStartMode(Session session) => SetStartMode(session, false);
    [CustomAction]
    public static ActionResult RollbackStartMode(Session session) => SetStartMode(session, true);
    private static ActionResult SetStartMode(Session session, bool rollback)
    {
        IntPtr manager = IntPtr.Zero, service = IntPtr.Zero;
        try
        {
            manager = OpenSCManager(null, null, 1);
            service = OpenService(manager, "KommitEye", 2);
            if (service == IntPtr.Zero || !ChangeServiceConfig(service, 0xffffffff, uint.Parse(session.CustomActionData["mode"]), 0xffffffff, null, null, IntPtr.Zero, null, null, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return ActionResult.Success;
        }
        catch { return rollback ? ActionResult.Success : Error(session, "Unable to configure automatic service startup."); }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
    }

    [CustomAction]
    public static ActionResult WriteServiceConfig(Session session)
    {
        try
        {
            var values = session.CustomActionData;
            // The installed executable owns initialization in every hosting mode.
            var arguments = "--initialize-config --config " + Quote(values["path"]) +
                " --data-dir " + Quote(values["data"]) + " --urls " + Quote(values["urls"]) +
                " --git-path " + Quote(values["git"]) + " --credential-protection " + Quote(values["protection"]);
            using (var child = Process.Start(new ProcessStartInfo(values["exe"], arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(30000)) { child.Kill(); throw new IOException("Configuration initialization timed out: " + values["path"]); }
                session.Log(output.GetAwaiter().GetResult());
                if (child.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult());
            }
            return ActionResult.Success;
        }
        catch (Exception error) { return Error(session, "Unable to initialize server configuration: " + error.Message); }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"").TrimEnd('\\') + "\"";

    [CustomAction]
    public static ActionResult OpenBrowser(Session session)
    {
        try { Process.Start(new ProcessStartInfo("http://localhost:" + SetupValidation.Port(session["PORT"])) { UseShellExecute = true }); }
        catch { /* Browser launch must never invalidate a completed installation. */ }
        return ActionResult.Success;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string path, string group, IntPtr tag, string dependencies, string account, string password, string display);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
