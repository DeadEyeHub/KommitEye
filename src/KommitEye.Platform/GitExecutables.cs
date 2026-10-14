namespace KommitEye.Platform;

public static class GitExecutables
{
    public static IGitExecutables Current { get; } = OperatingSystem.IsWindows()
        ? new WindowsGitExecutables() : new PosixGitExecutables();
}

public sealed class WindowsGitExecutables : IGitExecutables
{
    public string GitFileName => "git.exe";
    public bool SupportsPutty => true;
    public string? FindAgentSsh()
    {
        var windows = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        var path = Path.Combine(windows, "System32", "OpenSSH", "ssh.exe");
        return File.Exists(path) ? path : null;
    }
    public string? FindPlink()
    {
        var candidates = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var directory = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory, "PuTTY", "plink.exe"));
        }
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "plink.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class PosixGitExecutables : IGitExecutables
{
    public string GitFileName => "git";
    public bool SupportsPutty => false;
    // OpenSSH inherits SSH_AUTH_SOCK from the host process.
    public string FindAgentSsh() => "ssh";
    public string? FindPlink() => null;
}
