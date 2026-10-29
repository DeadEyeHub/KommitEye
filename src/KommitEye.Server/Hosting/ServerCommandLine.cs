using System.Globalization;
using System.Reflection;

namespace KommitEye.Server.Hosting;

public static class ServerCommandLine
{
    private static readonly string[] Values = ["--config", "--urls", "--port", "--data-dir", "--git-path", "--credential-protection", "--service-start", "--service-account", "--service-executable", "--service-snapshot", "--restore-service"];
    private static readonly string[] Flags = ["--help", "-h", "--initialize-config", "--install-service", "--uninstall-service", "--start-service", "--stop-service", "--update-service", "--service-password-stdin"];
    private static readonly string[] Commands = ["--initialize-config", "--install-service", "--uninstall-service", "--start-service", "--stop-service", "--service-snapshot", "--restore-service"];
    public static string AllowedOptions => string.Join(", ", Flags.Concat(Values));

    public static string[] Validate(string[] args)
    {
        var result = new List<string>();
        var options = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            var parts = args[i].Split('=', 2);
            var name = parts[0];
            if (!Flags.Contains(name) && !Values.Contains(name)) throw new ArgumentException($"Unknown option '{name}'.");
            if (options.ContainsKey(name)) throw new ArgumentException($"Option '{name}' was specified more than once.");
            string? value = null;
            if (Values.Contains(name))
            {
                if (parts.Length == 2) value = parts[1];
                else if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) value = args[++i];
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} requires a value.");
            }
            else if (parts.Length != 1) throw new ArgumentException($"{name} does not accept a value.");
            options.Add(name, value);
            result.Add(name);
            if (value is not null) result.Add(value);
        }
        if (options.ContainsKey("--help") || options.ContainsKey("-h")) return result.ToArray();
        var commands = Commands.Where(options.ContainsKey).ToArray();
        if (commands.Length > 1) throw new ArgumentException("Choose exactly one command.");
        var command = commands.SingleOrDefault();
        foreach (var name in new[] { "--service-start", "--service-account", "--service-executable", "--update-service" })
            if (options.ContainsKey(name) && command != "--install-service") throw new ArgumentException($"{name} requires --install-service.");
        if (options.ContainsKey("--service-password-stdin") && command is not ("--install-service" or "--restore-service"))
            throw new ArgumentException("--service-password-stdin requires --install-service or --restore-service.");
        if (command is not (null or "--initialize-config" or "--install-service"))
            foreach (var name in new[] { "--config", "--urls", "--port", "--data-dir", "--git-path", "--credential-protection" })
                if (options.ContainsKey(name)) throw new ArgumentException($"{name} cannot be used with {command}.");
        if (options.TryGetValue("--port", out var port))
        {
            if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
                throw new ArgumentException("--port must be an integer from 1 to 65535.");
            if (options.ContainsKey("--urls")) throw new ArgumentException("Use either --port or --urls, not both.");
        }
        if (options.TryGetValue("--service-start", out var startup) && startup is not ("automatic" or "manual"))
            throw new ArgumentException("--service-start must be automatic or manual.");
        if (options.TryGetValue("--credential-protection", out var protection) && protection is not ("CurrentUser" or "LocalMachine"))
            throw new ArgumentException("--credential-protection must be CurrentUser or LocalMachine.");
        if (options.TryGetValue("--urls", out var urls))
            foreach (var address in urls!.Split(';'))
            {
                var normalized = address.Replace("://+:", "://localhost:").Replace("://*:", "://localhost:");
                if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
                    throw new ArgumentException("--urls requires HTTP/HTTPS listening addresses separated by ';', without credentials, paths, queries or fragments.");
            }
        return result.ToArray();
    }

    public static string Help => $"""
        KommitEye Server {typeof(ServerCommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}

        Usage: KommitEye.Server.exe [options]
               KommitEye.Server.exe <command> [options]
        Without a command, runs the server in the current terminal.

        Options (values accept --name value or --name=value):
          --help, -h                    Show help and exit without changing anything.
          --config <path>               Configuration file path; created if missing.
          --urls <addresses>            HTTP/HTTPS listening URLs separated by ';'.
          --port <1-65535>               Override the port on all configured addresses.
                                        Preserves hosts/schemes; cannot combine with --urls.
          --data-dir <path>             Data and report directory.
          --git-path <path>             Git executable path (or '{KommitEye.Platform.GitExecutables.Current.GitFileName}' for PATH discovery).
          --credential-protection <mode> CurrentUser (default) or LocalMachine.
          --initialize-config           Create missing configuration and exit; preserve existing files.

        Windows service commands (Administrator terminal required):
          --install-service             Register KommitEye; does not start it.
          --uninstall-service           Stop/remove service; preserve configuration and data.
          --start-service               Start service.
          --stop-service                Stop service.
        Installation options:
          --service-start <mode>        automatic (default) or manual.
          --service-account <account>   New service: NT SERVICE\KommitEye.
                                        Existing account is preserved, never silently changed.
        Installer/advanced options:
          --service-executable <path>   Executable to register with --install-service.
          --update-service              Allow updating the registered executable/config path.
          --service-password-stdin      Read account password from stdin; install/restore only.
          --service-snapshot <path>     Save current service registration/state and exit.
          --restore-service <path>      Restore a saved service snapshot and exit.

        Defaults on this platform:
          Config: {ServerHostOptions.DefaultConfigurationPath}
          Data:   {ServerHostOptions.DefaultDataDirectory}
          URL:    http://127.0.0.1:5080
        Precedence: command line > server.json > supported environment variables > defaults.
        Environment fallbacks: KOMMITEYE_DATA_DIR and ASPNETCORE_URLS (when file fields are null).
        Existing server.json is never overwritten. Overrides apply to this process;
        edit server.json to change subsequent service starts. Restart to apply changes.

        Examples:
          KommitEye.Server.exe --help
          KommitEye.Server.exe --config "D:\KommitEye\server.json"
          KommitEye.Server.exe --port 5081
          KommitEye.Server.exe --urls "http://0.0.0.0:5080"
        Exit codes: 0 success/help; 2 invalid arguments; 1 startup/service operation failure.
        """;
}
