using System.Diagnostics;
using KommitEye.Models;
using KommitEye.Services;
using Localization = KommitEye.Services.Localization;

namespace KommitEye;

public partial class MainWindow
{
    // Runtime identity is independent of the selected tab and survives project switching.
    private sealed class ProjectRuntime
    {
        public required ReviewRunner Runner { get; init; }
        public required GitService Git { get; init; }
        public required PersistentLog Journal { get; init; }
        public required PersistentLog DetailsLog { get; init; }
        public string Label = "";
        public string DisplayName = "";
        public string Identity = "";
        public string WorkTree = "";
        public string Branch = "";
        public string Status = "";
        public string Commit = "-", Subject = "", Details = "";
        public string? UsageKey;
        public int? Remaining;
        public DateTimeOffset? NextRun;
        public bool AutomaticStopPending;
        public readonly Stopwatch CommitClock = new(), AgentClock = new();
        public TimeSpan AgentTimeLimit = ModelClient.ReviewTimeLimit;
    }

    private readonly List<ProjectRuntime> _projectRuntimes = [];
    private ProjectRuntime _activeRuntime = null!;

    private void InitializeProjectRuntimes()
    {
        var catalog = _configuration.LoadProjects();
        foreach (var settings in catalog.Projects) _projectRuntimes.Add(CreateProjectRuntime(settings));
        _activeRuntime = _projectRuntimes[catalog.ActiveIndex];
    }

    private ProjectRuntime CreateProjectRuntime(AppSettings settings)
    {
        var git = new GitService();
        if (!Guid.TryParseExact(settings.ProjectId, "N", out var projectId))
            throw new InvalidDataException("Invalid project log ID.");
        var logDirectory = Path.Combine(AppPaths.DataDirectory, "logs", projectId.ToString("N"));
        var runtime = new ProjectRuntime {
            Git = git,
            DisplayName = settings.DisplayName,
            Journal = new PersistentLog(Path.Combine(logDirectory, "journal.log")),
            DetailsLog = new PersistentLog(Path.Combine(logDirectory, "model.log"),
                maxBytes: 32 * 1024 * 1024, maxTailLines: 10_000, maxTailCharacters: 4_000_000),
            Runner = new ReviewRunner(git, _model, _configuration, _stateStore, _reportWriter),
            Label = settings.RepositoryPath + " | " + settings.BranchRef,
            Status = Localization.Text("Stopped", "Остановлено")
        };
        var runner = runtime.Runner;
        runner.Log += message => runtime.Journal.Append($"[{runtime.Label}] {message}");
        runner.ModelLog += message => runtime.DetailsLog.Append($"[{runtime.Label}] {message}");
        git.Diagnostic += message => runtime.DetailsLog.Append($"[{runtime.Label}] {message}");
        git.TransferProgress += message => {
            runtime.Journal.Append($"[{runtime.Label}] Git fetch | {message}");
            Dispatch(() => runtime.Status = "Git fetch | " + message);
        };
        runner.StatusChanged += status => Dispatch(() => runtime.Status = status);
        runner.AutomaticStopped += () => Dispatch(() => runtime.AutomaticStopPending = true);
        runner.CommitChanged += commit => Dispatch(() => {
            runtime.Commit = commit;
            runtime.Subject = runtime.Details = "";
        });
        runner.CommitInfoChanged += commit => Dispatch(() => {
            runtime.Commit = commit.Sha;
            runtime.Subject = commit.Subject;
            runtime.Details = $"{commit.Author} · {commit.Date.LocalDateTime:g}";
        });
        runner.RemainingChanged += count => Dispatch(() => runtime.Remaining = count);
        runner.NextRunChanged += next => Dispatch(() => runtime.NextRun = next);
        runner.UsageReceived += item => {
            _tokenUsage.Record(item.Repository, item.Sha, item.Usage, DateTimeOffset.Now);
            Dispatch(() => runtime.UsageKey = TokenUsageStore.Key(item.Repository, item.Sha));
        };
        runner.Progress += progress => AppendProgress(runtime, progress);
        runner.Reviewed += reviewed => Dispatch(() => NotifyReviewed(reviewed));
        runner.OperationalEvent += notice => _mail.EnqueueOperational(runtime.DisplayName + " | " + runtime.Label, notice.Kind, notice.Detail);
        runner.Reviewed += _mail.Enqueue;
        return runtime;
    }

    private bool _startupAttempted;
    private async Task StartConfiguredProjectsAsync()
    {
        if (_startupAttempted) return;
        _startupAttempted = true;
        IsEnabled = false;
        try
        {
            var projects = _configuration.LoadProjects().Projects;
            var models = _configuration.LoadModels();
            for (var i = 0; i < projects.Count; i++)
            {
                var settings = projects[i];
                if (!settings.AutoStart || _exitRequested) continue;
                var runtime = _projectRuntimes[i];
                try
                {
                    var profile = models.Profiles.FirstOrDefault(p => p.Name == models.ActiveProfile)
                        ?? throw new InvalidOperationException("Select an active model profile first.");
                    var identity = await runtime.Git.GetRepositoryIdentityAsync(settings.RepositoryPath, CancellationToken.None);
                    var branch = await runtime.Git.ResolveBranchAsync(settings.RepositoryPath, settings.BranchRef, CancellationToken.None);
                    if (_exitRequested) break;
                    if (_projectRuntimes.Any(p => p != runtime && p.Runner.IsRunning &&
                        (p.WorkTree.Equals(identity.WorkTreeRoot, StringComparison.OrdinalIgnoreCase) ||
                         p.Identity.Equals(identity.CommonGitDirectory, StringComparison.OrdinalIgnoreCase) && p.Branch == branch)))
                        throw new InvalidOperationException("This repository/worktree is already running in another project.");
                    runtime.WorkTree = identity.WorkTreeRoot; runtime.Identity = identity.CommonGitDirectory; runtime.Branch = branch;
                    if (!runtime.Runner.Start(settings, profile)) throw new InvalidOperationException("Project is already running.");
                }
                catch (Exception e)
                {
                    runtime.Journal.Append("Autostart failed: " + GitService.SanitizeDiagnostic(e.Message));
                    _mail.EnqueueOperational(runtime.Label, "Project start failed", e.Message);
                }
            }
            SetRunningControls(_runner.IsRunning);
            ShowRuntimeDashboard();
        }
        finally { if (!_exitRequested) IsEnabled = true; }
    }

    private async Task ReserveProjectScopeAsync(AppSettings settings)
    {
        var identity = await _git.GetRepositoryIdentityAsync(settings.RepositoryPath, CancellationToken.None);
        var branch = await _git.ResolveBranchAsync(settings.RepositoryPath, settings.BranchRef, CancellationToken.None);
        if (_projectRuntimes.Any(p => p != _activeRuntime && p.Runner.IsRunning &&
            (string.Equals(p.WorkTree, identity.WorkTreeRoot, StringComparison.OrdinalIgnoreCase) ||
             (string.Equals(p.Identity, identity.CommonGitDirectory, StringComparison.OrdinalIgnoreCase) && p.Branch == branch))))
            throw new InvalidOperationException(Localization.Text(
                "This working copy or repository branch is already running in another project. Stop it first or use a different worktree and branch.",
                "Эта рабочая копия или ветка репозитория уже проверяется в другом проекте. Остановите его или выберите другой worktree и ветку."));
        _activeRuntime.Identity = identity.CommonGitDirectory;
        _activeRuntime.WorkTree = identity.WorkTreeRoot;
        _activeRuntime.Branch = branch;
        _activeRuntime.Label = identity.WorkTreeRoot + " | " + branch;
    }

    private void ShowRuntimeDashboard()
    {
        if (_activeRuntime is null) return;
        StatusRun.Text = _activeRuntime.Status;
        CommitRun.Text = _activeRuntime.Commit;
        CommitSubjectTextBlock.Text = _activeRuntime.Subject;
        CommitSubjectTextBlock.ToolTip = _activeRuntime.Subject;
        CommitDetailsText.Text = _activeRuntime.Details;
        for (var i = 0; i < _projectRuntimes.Count && i < ProjectButtons.Children.Count; i++)
            if (ProjectButtons.Children[i] is System.Windows.Controls.Button button)
                button.Content = ProjectCaption(i) + (_projectRuntimes[i].Runner.IsRunning ? " ▶" : "");
        if (_trayIcon is not null)
            _trayIcon.Text = TruncateTrayText(Localization.Format("KommitEye - running projects: {0}",
                "KommitEye - работают проектов: {0}", _projectRuntimes.Count(p => p.Runner.IsRunning)));
        CrashDiagnostics.Context = string.Join("\n", _projectRuntimes.Where(p => p.Runner.IsRunning)
            .Select(p => $"{p.Label}; commit: {p.Commit}"));
    }
}
