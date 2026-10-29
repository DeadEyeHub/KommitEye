using System.Diagnostics;
using System.Text.Json;
using KommitEye.Server.Hosting;

static class CommandLineChecks
{
    public static async Task RunAsync(string root, string executable)
    {
        var directory = Path.Combine(root, "cli must not create");
        var config = Path.Combine(directory, "server.json");
        async Task<(int Code, string Text)> Run(params string[] arguments)
        {
            var dll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(dll ? "dotnet" : executable)
                { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (dll) start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            return (process.ExitCode, await output + await error);
        }
        foreach (var help in new[] { "--help", "-h" })
        {
            var result = await Run("--config", config, "--install-service", help);
            Check(result.Code == 0 && result.Text.Contains("Usage:") && result.Text.Contains("--port") && !Directory.Exists(directory),
                "help exits without configuration or service changes: " + help);
        }
        foreach (var arguments in new string[][] {
            ["--unknown"], ["unexpected"], ["--port"], ["--port=0"], ["--port=65536"], ["--port=text"],
            ["--port=5081", "--urls=http://localhost:5080"], ["--install-service", "--uninstall-service"],
            ["--service-account=LocalSystem"], ["--help=true"], ["--urls", "--help"], ["--urls=not-a-url"],
            ["--install-service", "--service-start=bad"], ["--port=5081", "--port=5082"] })
        {
            var result = await Run(["--config", config, .. arguments]);
            Check(result.Code == 2 && result.Text.Contains("Allowed options:") && !Directory.Exists(directory),
                "invalid arguments fail before initialization: " + string.Join(' ', arguments));
        }
        var initialized = await Run("--initialize-config", "--config=" + config, "--port=5081");
        Check(initialized.Code == 0 && File.ReadAllText(config).Contains("http://127.0.0.1:5081"), "port saved in newly created configuration");
        foreach (var address in new[] { "http://0.0.0.0:5080", "http://+:5080", "http://*:5080", "http://[::1]:5080", "https://localhost/", "http://127.0.0.1:5080;http://[::1]:5080" })
        {
            File.WriteAllText(config, JsonSerializer.Serialize(new { Urls = address }));
            var original = File.ReadAllBytes(config);
            var options = ServerHostOptions.Load(["--config", config, "--port", "5082"]);
            Check(options.Urls!.Split(';').All(url => url.EndsWith(":5082")) &&
                options.Urls.Contains(address.Split(';')[0].Split("://")[0] + "://") &&
                original.SequenceEqual(File.ReadAllBytes(config)), "port override preserves configuration: " + address);
        }
        var normalized = ServerCommandLine.Validate(["--install-service", "--service-account=NT SERVICE\\KommitEye", "--update-service", "--service-start=manual"]);
        Check(normalized.Contains("NT SERVICE\\KommitEye") && normalized.Contains("manual"), "installer arguments remain supported and equals values normalized");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        File.WriteAllText(config, JsonSerializer.Serialize(new { Urls = "http://127.0.0.1:1", DataDirectory = Path.Combine(directory, "data") }));
        var before = File.ReadAllBytes(config);
        var isDll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var launch = new ProcessStartInfo(isDll ? "dotnet" : executable)
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (isDll) launch.ArgumentList.Add(executable);
        launch.Environment.Remove("KOMMITEYE_ADMIN_PASSWORD");
        launch.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE");
        launch.Environment.Remove("KOMMITEYE_KEY_CERTIFICATE");
        if (File.Exists(Path.Combine(root, "fixture.pfx")))
            launch.Environment["KOMMITEYE_KEY_CERTIFICATE"] = Path.Combine(root, "fixture.pfx");
        foreach (var arg in new[] { "--config", config, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) launch.ArgumentList.Add(arg);
        using var server = Process.Start(launch)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            while (true)
            {
                if (server.HasExited) throw new Exception("Port fixture exited: " + await stderr);
                try { using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/v1/setup", timeout.Token); if (response.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                await Task.Delay(100, timeout.Token);
            }
            Check(before.SequenceEqual(File.ReadAllBytes(config)), "server listens on --port without overwriting existing configuration");
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(); await stdout; await stderr;
        }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
