using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KommitEye.Models;

namespace KommitEye.Services;

public sealed class ReportWriter
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ReportStore _store;

    public ReportWriter() : this(new ReportStore(AppPaths.DataDirectory)) { }
    public ReportWriter(ReportStore store) => _store = store;

    public IReadOnlyList<ReportSummary> List() => _store.List();
    public string? Read(string name) => _store.Read(name);

    public string GetReportPath(string repositoryPath, string branch, string? repositoryIdentity = null)
    {
        var identityPath = Path.GetFullPath(repositoryIdentity ?? repositoryPath);
        var identityDirectory = new DirectoryInfo(identityPath);
        var repositoryName = identityDirectory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                             identityDirectory.Parent is not null
            ? identityDirectory.Parent.Name
            : new DirectoryInfo(repositoryPath).Name;
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{identityPath}|{branch}")))[..10];
        var fileName = SanitizeFileName($"{repositoryName}-{branch}-{identity}-review.md");
        return Path.Combine(AppPaths.ReportsDirectory, fileName);
    }

    public async Task AppendAsync(
        string repositoryPath,
        string branch,
        CommitInfo commit,
        ReviewResult result,
        bool manualReview,
        CancellationToken cancellationToken,
        string? repositoryIdentity = null)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var path = GetReportPath(repositoryPath, branch, repositoryIdentity);
            var legacyPath = repositoryIdentity is null
                ? path
                : GetReportPath(repositoryPath, branch);
            var markdown = ReportRenderer.Render(branch, commit, result, manualReview);
            await Task.Run(() => _store.Save(Path.GetFileName(path), Path.GetFileName(legacyPath),
                repositoryIdentity ?? repositoryPath, branch, commit, result, manualReview, markdown,
                cancellationToken), cancellationToken);
            result.ReportMarkdown = markdown;
            result.RepositoryIdentity = repositoryIdentity ?? repositoryPath;
            result.AuthorEmail = commit.AuthorEmail;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(string repositoryPath, string branch, string? repositoryIdentity = null)
    {
        await _writeLock.WaitAsync();
        try
        {
            var path = GetReportPath(repositoryPath, branch, repositoryIdentity);
            var legacy = GetReportPath(repositoryPath, branch);
            await Task.Run(() => _store.Clear(Path.GetFileName(path), Path.GetFileName(legacy),
                repositoryIdentity ?? repositoryPath, branch));
        }
        finally { _writeLock.Release(); }
    }

    public void Open(string repositoryPath, string branch, string? repositoryIdentity = null)
    {
        var path = GetReportPath(repositoryPath, branch, repositoryIdentity);
        var markdown = _store.ReadForRepository(Path.GetFileName(path),
            Path.GetFileName(GetReportPath(repositoryPath, branch)), repositoryIdentity ?? repositoryPath, branch);
        if (markdown is null)
            throw new FileNotFoundException(Localization.Text(
                "No report exists for the selected project yet.",
                "Для выбранного проекта пока нет отчета."), path);
        // Exports live outside the legacy import directory.
        path = Path.Combine(AppPaths.DataDirectory, "report-exports", Path.GetFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(character, '_');
        return fileName;
    }
}
