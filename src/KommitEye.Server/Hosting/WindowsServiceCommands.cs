using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace KommitEye.Server.Hosting;

// The only owner of SCM registration. MSI and scripts invoke these commands.
public static class WindowsServiceCommands
{
    public const string Name = "KommitEye";
    public const string DefaultAccount = @"NT SERVICE\KommitEye";
    private static readonly string[] Commands = ["--install-service", "--uninstall-service", "--start-service", "--stop-service", "--service-snapshot", "--restore-service"];
    public sealed record Snapshot(bool Exists, string Path = "", string Account = "", uint Start = 2, bool Running = false);

    public static int? Run(string[] args)
    {
        var commands = Commands.Where(args.Contains).ToArray();
        if (commands.Length == 0) return null;
        try
        {
            if (commands.Length != 1) throw new ArgumentException("Choose exactly one service operation.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service commands require Windows.");
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Run this command from an elevated Administrator terminal.");
            string? Option(string key)
            {
                var index = Array.IndexOf(args, key);
                if (index < 0) return null;
                if (index + 1 == args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException(key + " requires a value.");
                return args[index + 1];
            }
            var password = args.Contains("--service-password-stdin") ? Console.In.ReadLine() : null;
            switch (commands[0])
            {
                case "--install-service":
                    var config = ServerHostOptions.ResolveConfigurationPath(args);
                    var options = ServerHostOptions.Load(!File.Exists(config) && !args.Contains("--credential-protection")
                        ? [.. args, "--credential-protection", "LocalMachine"] : args);
                    var exe = Absolute(Option("--service-executable") ?? Environment.ProcessPath!);
                    if (!File.Exists(exe) || !Path.GetFileName(exe).Equals("KommitEye.Server.exe", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Register the published KommitEye.Server.exe, not dotnet.exe.");
                    var existing = Read();
                    var account = Option("--service-account") ?? (existing.Exists ? existing.Account : DefaultAccount);
                    var startup = Option("--service-start") ?? "automatic";
                    if (startup is not ("automatic" or "manual")) throw new ArgumentException("--service-start must be automatic or manual.");
                    var binary = Quote(exe) + " --config " + Quote(config);
                    if (existing.Exists && !existing.Path.Equals(binary, StringComparison.OrdinalIgnoreCase) && !args.Contains("--update-service"))
                        throw new InvalidOperationException("KommitEye is already registered with another executable/configuration. Use --update-service explicitly; its account will be preserved.");
                    Install(binary, account, startup == "automatic" ? 2u : 3u, password);
                    Console.WriteLine($"Service {Name} registered as {account}. Configuration: {config}. Data: {options.DataDirectory}. Folder permissions were not changed. Registration does not start the server.");
                    break;
                case "--uninstall-service": Remove(); break;
                case "--start-service": Start(); break;
                case "--stop-service": Stop(); break;
                case "--service-snapshot":
                    var snapshot = Absolute(Option("--service-snapshot")!);
                    using (var file = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write))
                    { JsonSerializer.Serialize(file, Read()); file.Flush(true); }
                    break;
                case "--restore-service":
                    var state = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Absolute(Option("--restore-service")!)))
                        ?? throw new InvalidDataException("Invalid service snapshot.");
                    if (!state.Exists) Remove();
                    else
                    {
                        Stop();
                        Install(state.Path, state.Account, state.Start, password);
                        if (state.Running) Start();
                    }
                    break;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"KommitEye service operation failed: {error.Message} No folder permissions were changed.");
            return 1;
        }
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("Use an absolute path without quotes or line breaks: " + path);
        return Path.GetFullPath(path);
    }
    private static string Quote(string value) => "\"" + Absolute(value) + "\"";
    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static Handle Manager(uint access = 1)
    {
        var handle = OpenSCManager(null, null, access);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    private static Handle? Open(Handle manager, uint access)
    {
        var handle = OpenService(manager, Name, access);
        if (!handle.IsInvalid) return handle;
        var code = Marshal.GetLastWin32Error(); handle.Dispose();
        if (code == 1060) return null;
        throw new Win32Exception(code);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Snapshot Read()
    {
        using var manager = Manager();
        using var service = Open(manager, 1);
        if (service is null) return new(false);
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        if (needed == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            Check(QueryServiceConfig(service, buffer, needed, out _));
            var config = Marshal.PtrToStructure<Config>(buffer);
            using var controller = new ServiceController(Name);
            return new(true, Marshal.PtrToStringUni(config.Path)!, Marshal.PtrToStringUni(config.Account)!, config.Start,
                controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Install(string binary, string account, uint start, string? password)
    {
        var before = Read();
        if (before.Exists && !before.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Existing service account '{before.Account}' differs from '{account}'. Account changes require an explicit administrator migration of credentials and permissions.");
        using var manager = Manager(3);
        using var service = Open(manager, 2);
        if (service is not null)
        {
            // Null account/password deliberately preserve the SCM credentials.
            Check(ChangeServiceConfig(service, uint.MaxValue, start, uint.MaxValue, binary, null, IntPtr.Zero, null, null, null, "KommitEye Review Server"));
        }
        else
        {
            using var created = CreateService(manager, Name, "KommitEye Review Server", 0xF01FF, 0x10, start, 1, binary,
                null, IntPtr.Zero, null, account, password);
            if (created.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Stop()
    {
        if (!Read().Exists) { Console.WriteLine("Service KommitEye is not registered."); return; }
        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        Console.WriteLine("Service KommitEye stopped.");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Start()
    {
        var state = Read();
        if (!state.Exists) throw new InvalidOperationException("Service KommitEye is not registered.");
        using var service = new ServiceController(Name);
        try
        {
            if (service.Status == ServiceControllerStatus.Running) return;
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
            Console.WriteLine("Service KommitEye started.");
        }
        catch (Exception error)
        {
            throw new IOException($"Cannot start service as '{state.Account}'. Check the Windows Application log for the failing path. The account needs read/execute access to the program and Git, read access to configuration, modify access to data and required repository access. {error.Message}", error);
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Remove()
    {
        Stop();
        using var manager = Manager();
        using (var service = Open(manager, 0x10000))
        {
            if (service is null) return;
            Check(DeleteService(service));
        }
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try { using var service = Open(manager, 1); if (service is null) { Console.WriteLine("Service KommitEye removed. Configuration, data, credentials and repositories preserved."); return; } }
            catch (Win32Exception error) when (error.NativeErrorCode == 1072) { }
            Thread.Sleep(200);
        }
        throw new IOException("Service is marked for deletion. Close Services consoles holding its handle and retry.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Config { public uint Type, Start, Error; public IntPtr Path, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Handle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Handle OpenService(Handle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Handle CreateService(Handle manager, string name, string display, uint access, uint type, uint start, uint error, string path, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(Handle service, uint type, uint start, uint error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(Handle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(Handle service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
