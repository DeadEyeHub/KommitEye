using System.Text.Json;
using System.Text.Json.Serialization;
using KommitEye.Platform;

namespace KommitEye.Server.Hosting;

public sealed class ServerHostOptions
{
    public string? DataDirectory { get; init; }
    public string? Urls { get; init; }
    public string? GitExecutable { get; init; }
    public string CredentialProtection { get; init; } = "CurrentUser";

    public static string DefaultDataDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server", "data")
        : "/var/lib/kommiteye/server";

    public static string DefaultConfigurationPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server", "server.json")
        : "/etc/kommiteye/server.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static string? Option(string[] args, string name)
    {
        for (var i = args.Length - 1; i >= 0; i--)
        {
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                var value = args[i][(name.Length + 1)..];
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " requires a value.");
                return value;
            }
            if (args[i] != name) continue;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--"))
                throw new ArgumentException(name + " requires a value.");
            return args[i + 1];
        }
        return null;
    }

    public static string ResolveConfigurationPath(string[] args) =>
        AbsolutePath(Option(args, "--config") ?? DefaultConfigurationPath, "--config");

    public static ServerHostOptions Load(string[] args)
    {
        var path = ResolveConfigurationPath(args);
        Console.WriteLine("Server configuration path: " + path);
        try
        {
            ServerHostOptions Read() => JsonSerializer.Deserialize<ServerHostOptions>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Server configuration must be a JSON object, not null.");
            ServerHostOptions config;
            var created = false;
            try { config = Read(); }
            catch (FileNotFoundException) { config = Create(); }
            catch (DirectoryNotFoundException) { config = Create(); }
            var resolved = Resolve(config, args);
            Console.WriteLine($"Server configuration {(created ? "created" : "loaded")}: {path}");
            return resolved;

            ServerHostOptions Create()
            {
                // Persist only explicit initialization choices, never environment variables or secrets.
                var initial = new ServerHostOptions
                {
                    DataDirectory = Option(args, "--data-dir"), Urls = Option(args, "--urls"),
                    GitExecutable = Option(args, "--git-path"),
                    CredentialProtection = Option(args, "--credential-protection") ?? "CurrentUser"
                };
                _ = Resolve(initial, args); // Reject invalid options before creating any file.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, initial, JsonOptions);
                        stream.Flush(true);
                    }
                    try { File.Move(temporary, path, overwrite: false); created = true; }
                    catch (IOException) when (File.Exists(path)) { /* Another startup won; read its file. */ }
                    return Read();
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Cannot initialize or read server configuration '{path}': {error.Message} Check the JSON settings and the account's file/directory permissions. Existing configuration and ACLs have not been replaced.", error);
        }
    }

    private static ServerHostOptions Resolve(ServerHostOptions config, string[] args)
    {
        string? Option(string name) => ServerHostOptions.Option(args, name);
        var data = Option("--data-dir") ?? config.DataDirectory ?? Environment.GetEnvironmentVariable("KOMMITEYE_DATA_DIR");
        if (data is null) data = DefaultDataDirectory;
        else if (string.IsNullOrWhiteSpace(data)) throw new ArgumentException("DataDirectory cannot be empty.");
        var urls = Option("--urls") ?? config.Urls ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:5080";
        if (string.IsNullOrWhiteSpace(urls)) throw new ArgumentException("Urls cannot be empty.");
        foreach (var address in urls.Split(';'))
        {
            var normalized = address.Replace("://+:", "://localhost:").Replace("://*:", "://localhost:");
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
                throw new ArgumentException("Urls must contain HTTP/HTTPS listening addresses, separated by semicolons, without credentials, paths, query strings or fragments.");
        }
        var git = Option("--git-path") ?? config.GitExecutable;
        if (git is not null)
        {
            git = AbsolutePath(git, "GitExecutable");
            if (!File.Exists(git) || !Path.GetFileName(git).Equals(GitExecutables.Current.GitFileName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException($"GitExecutable must point to an existing {GitExecutables.Current.GitFileName}.");
        }
        var protection = Option("--credential-protection") ?? config.CredentialProtection;
        if (protection is not ("CurrentUser" or "LocalMachine"))
            throw new ArgumentException("CredentialProtection must be CurrentUser or LocalMachine.");
        if (!OperatingSystem.IsWindows() && protection == "LocalMachine")
            throw new ArgumentException("LocalMachine credential protection is only available on Windows.");
        return new ServerHostOptions { DataDirectory = AbsolutePath(data, "DataDirectory"), Urls = urls,
            GitExecutable = git, CredentialProtection = protection };
    }

    private static string AbsolutePath(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(name + " must be an absolute path.");
        return Path.GetFullPath(path);
    }
}
