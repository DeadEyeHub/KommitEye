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

    public static ServerHostOptions Load(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--"))
                throw new ArgumentException(name + " requires a value.");
            return args[index + 1];
        }
        var configPath = Option("--config");
        var config = configPath is null ? new ServerHostOptions() :
            JsonSerializer.Deserialize<ServerHostOptions>(File.ReadAllText(AbsolutePath(configPath, "--config")), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("Server configuration cannot be null.");
        var data = Option("--data-dir") ?? config.DataDirectory ?? Environment.GetEnvironmentVariable("KOMMITEYE_DATA_DIR");
        if (data is null) data = DefaultDataDirectory;
        else if (string.IsNullOrWhiteSpace(data)) throw new ArgumentException("DataDirectory cannot be empty.");
        var urls = Option("--urls") ?? config.Urls ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:5080";
        if (string.IsNullOrWhiteSpace(urls)) throw new ArgumentException("Urls cannot be empty.");
        var git = Option("--git-path") ?? config.GitExecutable;
        if (git is not null)
        {
            git = AbsolutePath(git, "GitExecutable");
            if (!File.Exists(git) || !Path.GetFileName(git).Equals(GitExecutables.Current.GitFileName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException($"GitExecutable must point to an existing {GitExecutables.Current.GitFileName}.");
        }
        if (config.CredentialProtection is not ("CurrentUser" or "LocalMachine"))
            throw new ArgumentException("CredentialProtection must be CurrentUser or LocalMachine.");
        if (!OperatingSystem.IsWindows() && config.CredentialProtection == "LocalMachine")
            throw new ArgumentException("LocalMachine credential protection is only available on Windows.");
        return new ServerHostOptions { DataDirectory = AbsolutePath(data, "DataDirectory"), Urls = urls,
            GitExecutable = git, CredentialProtection = config.CredentialProtection };
    }

    private static string AbsolutePath(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException(name + " must be an absolute path.");
        return Path.GetFullPath(path);
    }
}
