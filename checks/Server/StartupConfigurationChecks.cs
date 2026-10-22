using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;

static class StartupConfigurationChecks
{
    public static async Task RunAsync(string root, string executable)
    {
        var directory = Path.Combine(root, "startup-config");
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "server.json");
        var beforeAcl = OperatingSystem.IsWindows()
            ? new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) : null;
        async Task<(int Code, string Log)> Run()
        {
            var dll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(dll ? "dotnet" : executable)
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (dll) start.ArgumentList.Add(executable);
            foreach (var argument in new[] { "--initialize-config", "--config", config }) start.ArgumentList.Add(argument);
            start.Environment["KOMMITEYE_DATA_DIR"] = Path.Combine(directory, "environment-data");
            start.Environment["KOMMITEYE_ADMIN_PASSWORD"] = "fixture-secret-never-persist";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, await output + await error);
        }
        var created = await Run();
        Check(created.Code == 0 && created.Log.Contains("created: " + config), "EXE initializes missing config and logs full path");
        var original = File.ReadAllBytes(config);
        using (var json = JsonDocument.Parse(original))
            Check(json.RootElement.EnumerateObject().Count() == 4 &&
                json.RootElement.GetProperty("DataDirectory").GetString() == KommitEye.Server.Hosting.ServerHostOptions.DefaultDataDirectory &&
                json.RootElement.GetProperty("Urls").GetString() == "http://127.0.0.1:5080" &&
                json.RootElement.GetProperty("GitExecutable").GetString() == KommitEye.Platform.GitExecutables.Current.GitFileName &&
                json.RootElement.GetProperty("CredentialProtection").GetString() == "CurrentUser",
                "generated config contains supported defaults, not environment values or secrets");
        Check(!Directory.Exists(Path.Combine(directory, "environment-data")), "initialization-only mode does not start data stores");
        var loaded = await Run();
        Check(loaded.Code == 0 && loaded.Log.Contains("loaded: " + config) && original.SequenceEqual(File.ReadAllBytes(config)),
            "EXE reload preserves existing bytes");
        File.WriteAllText(config, "{invalid");
        var invalid = await Run();
        Check(invalid.Code == 1 && invalid.Log.Contains(config) && File.ReadAllText(config) == "{invalid",
            "EXE rejects invalid JSON with full path and leaves it untouched");
        using (var locked = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unreadable = await Run();
            Check(unreadable.Code == 1 && unreadable.Log.Contains(config) && unreadable.Log.Contains("permissions"),
                "EXE reports unreadable configuration without replacing it");
        }
        if (OperatingSystem.IsWindows())
            Check(beforeAcl == new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                "initialization and failures preserve directory ACL and inheritance");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
