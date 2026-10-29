using System.Diagnostics;
using KommitEye.Models;
using KommitEye.Services;

static class SubmoduleFetchChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kommiteye-submodule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task<string> Git(string path, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await error);
            return (await output).Trim();
        }
        async Task Commit(string path, string message) => await Git(path, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-am", message);
        var module = Path.Combine(root, "module"); Directory.CreateDirectory(module);
        await Git(module, "init", "-b", "main"); await Commit(module, "first module commit");
        var parent = Path.Combine(root, "parent"); Directory.CreateDirectory(parent);
        await Git(parent, "init", "-b", "main");
        await Git(parent, "-c", "protocol.file.allow=always", "submodule", "add", module, "child");
        await Commit(parent, "add module");
        var clone = Path.Combine(root, "clone");
        await Git(root, "-c", "protocol.file.allow=always", "clone", "--recurse-submodules", parent, clone);
        var child = Path.Combine(clone, "child"); await Git(child, "config", "protocol.file.allow", "always");
        var original = await Git(child, "rev-parse", "refs/remotes/origin/main");
        await Commit(module, "new module commit without parent change");
        var target = await Git(module, "rev-parse", "HEAD");
        var git = new GitService();
        var settings = new AppSettings { RepositoryPath = clone, BranchRef = "refs/heads/main", FetchSubmodulesAlways = true };
        async Task Fetch() { var result = await git.FetchAsync(clone, settings, default); if (result.ExitCode != 0) throw new Exception(result.Error); }
        await Fetch();
        if (await Git(child, "rev-parse", "refs/remotes/origin/main") != original) throw new Exception("Disabled submodule fetch contacted remote");
        settings.FetchSubmodules = true; settings.FetchSubmodulesAlways = false;
        await Fetch();
        if (await Git(child, "rev-parse", "refs/remotes/origin/main") != original) throw new Exception("On-demand fetched an unneeded module commit");
        settings.FetchSubmodulesAlways = true;
        await Fetch();
        if (await Git(child, "rev-parse", "refs/remotes/origin/main") != target) throw new Exception("Always mode did not fetch module");
        if (await Git(child, "rev-parse", "HEAD") != original) throw new Exception("Fetch changed submodule checkout");
        Console.WriteLine("PASS: real local Git submodules: disabled, on-demand and always; fetch preserves checkout.");
    }
}
