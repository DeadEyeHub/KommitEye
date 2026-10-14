using KommitEye.Models;
using KommitEye.Services;

namespace KommitEye.Server;

public static class RepositoryApi
{
    public static void Map(RouteGroupBuilder api)
    {
        // Administrator-only metadata browsing. File contents (including keys) never leave the server.
        api.MapPost("/repositories/browse", (BrowseRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.Ok(new { path = "", parent = (string?)null, entries = Directory.GetLogicalDrives().Select(p => new { name = p, path = p, directory = true }), truncated = false });
            var path = Path.GetFullPath(request.Path);
            if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(path)) throw new ArgumentException("Directory does not exist on the server.");
            var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                .Where(entry => request.Files || entry is DirectoryInfo)
                .Take(1001).ToArray();
            return Results.Ok(new { path, parent = Directory.GetParent(path)?.FullName,
                entries = entries.Take(1000).OrderByDescending(e => e is DirectoryInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new { name = e.Name, path = e.FullName, directory = e is DirectoryInfo }), truncated = entries.Length > 1000 });
        });
        api.MapPost("/repositories/detect", async (AppSettings settings, CancellationToken token) =>
            new { mode = await new GitService().DetectAuthenticationModeAsync(settings.RepositoryPath, token, settings.BranchRef) });
        api.MapPost("/repositories/test", async (AppSettings settings, CancellationToken token) =>
        {
            ValidateMode(settings);
            var git = new GitService();
            if (settings.GitAuthenticationMode == "auto") settings.GitAuthenticationMode = await git.DetectAuthenticationModeAsync(settings.RepositoryPath, token, settings.BranchRef);
            await git.TestRemoteAccessAsync(settings.RepositoryPath, settings, token);
            return new { remote = await git.GetSelectedRemoteSummaryAsync(settings.RepositoryPath, settings.BranchRef, token) };
        });
    }

    public static void ValidateMode(AppSettings settings)
    {
        if (settings.GitAuthenticationMode is not ("auto" or "ssh-agent" or "ssh-key" or "putty-key" or "https"))
            throw new ArgumentException("Select a supported Git authentication mode.");
    }
    public sealed record BrowseRequest(string? Path, bool Files = false);
}
