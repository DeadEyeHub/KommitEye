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
        Reject(() => ServerHostOptions.Load(["--config", Path.Combine(root, "missing.json")]), "missing config does not fall back to a user directory");
        File.WriteAllText(path, "{invalid");
        Reject(() => ServerHostOptions.Load(["--config", path]), "invalid JSON rejected");
        File.WriteAllText(path, "{\"DataDirectry\":\"typo\"}");
        Reject(() => ServerHostOptions.Load(["--config", path]), "unknown configuration property rejected");
        File.WriteAllText(path, "{\"DataDirectory\":\"relative\"}");
        Reject(() => ServerHostOptions.Load(["--config", path]), "relative data directory rejected");
        if (OperatingSystem.IsWindows())
            Check(ServerHostOptions.DefaultDataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KommitEye", "Server", "data"), "Windows server defaults to ProgramData, not a user profile");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or IOException or JsonException) { Console.WriteLine("PASS: " + message); return; }
        throw new Exception(message);
    }
}
