using System.Text.Json;
using KommitEye.Server.Hosting;

static class HostConfigurationChecks
{
    public static void Run(string root, string git)
    {
        var path = Path.Combine(root, "server.json");
        var data = Path.Combine(root, "configured-data");
        File.WriteAllText(path, JsonSerializer.Serialize(new { DataDirectory = data, Urls = "http://127.0.0.1:5081", GitExecutable = git }));
        var options = ServerHostOptions.Load(["--config", path]);
        Check(options.DataDirectory == data && options.Urls == "http://127.0.0.1:5081" && options.GitExecutable == git, "server reads paths and address from explicit JSON config");
        Check(ServerHostOptions.Load(["--config", path, "--data-dir", root]).DataDirectory == root, "explicit CLI overrides JSON config");
        Reject(() => ServerHostOptions.Load(["--config"]), "missing config argument rejected");
        var missing = Path.Combine(root, "nested", "missing.json");
        var initialized = ServerHostOptions.Load(["--config", missing, "--data-dir", data]);
        Check(File.Exists(missing) && initialized.DataDirectory == data, "explicit missing config creates parents and valid JSON");
        var original = File.ReadAllBytes(missing);
        ServerHostOptions.Load(["--config=" + missing, "--data-dir", root]);
        Check(original.SequenceEqual(File.ReadAllBytes(missing)), "subsequent starts preserve configuration byte for byte");
        Check(ServerHostOptions.ResolveConfigurationPath([]) == ServerHostOptions.DefaultConfigurationPath, "console and service share the default path");
        var concurrent = Path.Combine(root, "concurrent", "server.json");
        Parallel.For(0, 8, _ => ServerHostOptions.Load(["--config", concurrent]));
        using (JsonDocument.Parse(File.ReadAllText(concurrent))) { }
        Check(Directory.GetFiles(Path.GetDirectoryName(concurrent)!).Length == 1, "concurrent initialization is atomic and leaves no temporary files");
        var blockedParent = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedParent, "keep");
        RejectPath(() => ServerHostOptions.Load(["--config", Path.Combine(blockedParent, "server.json")]), Path.Combine(blockedParent, "server.json"));
        Check(File.ReadAllText(blockedParent) == "keep", "failed creation preserves existing filesystem content");
        using (var locked = new FileStream(missing, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            RejectPath(() => ServerHostOptions.Load(["--config", missing]), missing);
        Check(original.SequenceEqual(File.ReadAllBytes(missing)), "unreadable configuration is not replaced");
        var environmentData = Environment.GetEnvironmentVariable("KOMMITEYE_DATA_DIR");
        var environmentUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        try
        {
            Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", root);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:5099");
            Check(ServerHostOptions.Load(["--config", concurrent]).DataDirectory == root, "null fields fall back to environment");
            Check(ServerHostOptions.Load(["--config", path]).Urls == "http://127.0.0.1:5081", "file overrides environment");
            Check(ServerHostOptions.Load(["--config", path, "--urls", "http://127.0.0.1:5098"]).Urls!.EndsWith(":5098"), "CLI overrides file and environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOMMITEYE_DATA_DIR", environmentData);
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", environmentUrls);
        }
        File.WriteAllText(path, "{invalid");
        Reject(() => ServerHostOptions.Load(["--config", path]), "invalid JSON rejected");
        Check(File.ReadAllText(path) == "{invalid", "invalid JSON is preserved");
        File.WriteAllText(path, "{\"DataDirectry\":\"typo\"}");
        Reject(() => ServerHostOptions.Load(["--config", path]), "unknown configuration property rejected");
        File.WriteAllText(path, "{\"DataDirectory\":\"relative\"}");
        Reject(() => ServerHostOptions.Load(["--config", path]), "relative data directory rejected");
        File.WriteAllText(path, "{\"DataDirectory\":\"\"}");
        Reject(() => ServerHostOptions.Load(["--config", path]), "empty data directory does not silently use defaults");
        if (OperatingSystem.IsWindows())
            Check(ServerHostOptions.DefaultDataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server", "data"), "Windows server defaults to ProgramData, not a user profile");
    }
    private static void RejectPath(Action action, string path)
    {
        try { action(); }
        catch (InvalidDataException error) when (error.Message.Contains(path))
        { Console.WriteLine("PASS: filesystem failure reports full configuration path"); return; }
        throw new Exception("Expected actionable configuration error for " + path);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or JsonException) { Console.WriteLine("PASS: " + message); return; }
        throw new Exception(message);
    }
}
