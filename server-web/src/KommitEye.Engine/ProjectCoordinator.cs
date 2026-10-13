using KommitEye.Models;

namespace KommitEye.Services;

// One coordinator owns all repository workers. HTTP connections never own a review.
public sealed class ProjectCoordinator : IAsyncDisposable
{
    private sealed class Runtime
    {
        public required ReviewRunner Runner;
        public required GitService Git;
        public required PersistentLog Journal;
        public required PersistentLog Details;
        public required ProjectStatus Status;
        public string WorkTree = "", Identity = "", Branch = "";
        public CancellationTokenSource? Cancellation;
        public Task? Task;
        public ReviewJob? Job;
    }
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Dictionary<string, Runtime> _runtimes = [];
    private readonly ConfigurationStore _configuration;
    private readonly ModelClient _model;
    private readonly StateStore _state;
    private readonly ReportWriter _reports;
    private readonly MailNotifications _mail;
    private readonly JobStore _jobs;
    private readonly TokenUsageStore _usage = new(Path.Combine(AppPaths.DataDirectory, "token-usage.json"));
    public event Action<ServerEvent>? Changed;
    public ProjectCoordinator(ConfigurationStore configuration, ModelClient model, StateStore state, ReportWriter reports, MailNotifications mail, JobStore jobs)
    {
        _configuration = configuration; _model = model; _state = state; _reports = reports; _mail = mail; _jobs = jobs;
        configuration.InitializeProjects();
        foreach (var project in configuration.LoadProjects().Projects) _runtimes.Add(project.ProjectId, Create(project));
    }
    private Runtime Create(AppSettings project)
    {
        var git = new GitService(); var runner = new ReviewRunner(git, _model, _configuration, _state, _reports);
        var directory = Path.Combine(AppPaths.DataDirectory, "logs", project.ProjectId);
        var runtime = new Runtime { Git = git, Runner = runner,
            Journal = new(Path.Combine(directory, "journal.log")), Details = new(Path.Combine(directory, "model.log")),
            Status = new(project.ProjectId, "stopped", "", "idle", false, null, null) };
        void Update(Func<ProjectStatus, ProjectStatus> update)
        {
            lock (_stateGate) runtime.Status = update(runtime.Status);
            Changed?.Invoke(new(project.ProjectId, "status", "", DateTimeOffset.UtcNow));
        }
        runner.Log += message => { runtime.Journal.Append(message); Changed?.Invoke(new(project.ProjectId, "log", message, DateTimeOffset.UtcNow)); };
        runner.ModelLog += runtime.Details.Append;
        git.Diagnostic += runtime.Details.Append;
        runner.StatusChanged += status => Update(s => s with { Status = status });
        runner.CommitChanged += sha => Update(s => s with { Commit = sha });
        runner.RemainingChanged += count => Update(s => s with { Remaining = count });
        runner.NextRunChanged += next => Update(s => s with { NextRun = next });
        runner.Progress += progress => Update(s => s with { Stage = progress.Stage.ToString(), Commit = progress.Commit });
        runner.UsageReceived += item => _usage.Record(item.Repository, item.Sha, item.Usage, DateTimeOffset.UtcNow);
        runner.Reviewed += _mail.Enqueue;
        runner.Reviewed += result => Changed?.Invoke(new(project.ProjectId, "report", result.Sha, DateTimeOffset.UtcNow));
        return runtime;
    }
    public async Task<ProjectSnapshot> SnapshotAsync()
    {
        await _commands.WaitAsync();
        try { lock (_stateGate) return new(_jobs.Revision(), _configuration.LoadProjects().Projects, _runtimes.Values.Select(r => r.Status).ToArray()); }
        finally { _commands.Release(); }
    }
    public async Task SaveProjectAsync(AppSettings project, long revision)
    {
        await _commands.WaitAsync();
        try
        {
            if (revision != _jobs.Revision()) throw new InvalidOperationException("Settings changed in another browser. Reload before saving.");
            if (project.PollIntervalSeconds is < 5 or > 86400) throw new ArgumentException("Polling interval must be 5–86400 seconds.");
            _ = new DailyModelSchedule(project);
            if (string.IsNullOrWhiteSpace(project.RepositoryPath)) throw new ArgumentException("Enter a repository path on the server.");
            var git = new GitService();
            await git.ValidateRepositoryAsync(project.RepositoryPath, default);
            var branches = await git.GetBranchesAsync(project.RepositoryPath, default);
            if (!branches.Contains(project.BranchRef) && branches.Contains("refs/heads/" + project.BranchRef)) project.BranchRef = "refs/heads/" + project.BranchRef;
            project.BranchRef = await git.ResolveBranchAsync(project.RepositoryPath, project.BranchRef, default);
            if (project.GitAuthenticationMode == "auto") project.GitAuthenticationMode = await git.DetectAuthenticationModeAsync(project.RepositoryPath, default, project.BranchRef);
            var catalog = _configuration.LoadProjects();
            if (string.IsNullOrEmpty(project.ProjectId)) project.ProjectId = Guid.NewGuid().ToString("N");
            var index = catalog.Projects.FindIndex(p => p.ProjectId == project.ProjectId);
            if (_runtimes.TryGetValue(project.ProjectId, out var current) && current.Status.Running)
                throw new InvalidOperationException("Stop the project before changing its settings.");
            if (index < 0) catalog.Projects.Add(project); else catalog.Projects[index] = project;
            // Invalidate stale drafts before replacing settings, including after an interrupted save.
            _jobs.AdvanceRevision(); _configuration.SaveProjects(catalog);
            lock (_stateGate) _runtimes[project.ProjectId] = Create(project);
        }
        finally { _commands.Release(); }
    }
    public async Task<ReviewJob> StartAsync(string id, string? sha, string requestKey)
    {
        if (!Guid.TryParse(requestKey, out _)) throw new ArgumentException("A UUID request key is required.");
        await _commands.WaitAsync();
        try
        {
            var previous = _jobs.List().FirstOrDefault(j => j.RequestKey == requestKey);
            if (previous is not null)
            {
                if (previous.ProjectId != id || previous.Revision != (sha ?? "")) throw new InvalidOperationException("Request key already used for a different operation.");
                return previous;
            }
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Project not found.");
            if (runtime.Status.Running) throw new InvalidOperationException("Project is already running.");
            var project = _configuration.LoadProjects().Projects.Single(p => p.ProjectId == id);
            var models = _configuration.LoadModels();
            var profile = models.Profiles.FirstOrDefault(p => p.Name == models.ActiveProfile) ?? throw new ArgumentException("Select an active model profile first.");
            profile.Parameters.Validate();
            if (string.IsNullOrWhiteSpace(profile.Model) || !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException("Configure the model name and HTTP endpoint first.");
            var identity = await runtime.Git.GetRepositoryIdentityAsync(project.RepositoryPath, default);
            var branch = await runtime.Git.ResolveBranchAsync(project.RepositoryPath, project.BranchRef, default);
            if (_runtimes.Values.Any(r => r != runtime && r.Status.Running &&
                (r.WorkTree.Equals(identity.WorkTreeRoot, StringComparison.OrdinalIgnoreCase) ||
                r.Identity.Equals(identity.CommonGitDirectory, StringComparison.OrdinalIgnoreCase) && r.Branch == branch)))
                throw new InvalidOperationException("This repository/worktree is already running in another project.");
            runtime.WorkTree = identity.WorkTreeRoot; runtime.Identity = identity.CommonGitDirectory; runtime.Branch = branch;
            var job = new ReviewJob(Guid.NewGuid().ToString("N"), id, requestKey, sha ?? "", sha is null ? "monitor" : "review", "running", DateTimeOffset.UtcNow);
            _jobs.Save(job); runtime.Job = job;
            runtime.Cancellation?.Dispose(); runtime.Cancellation = new();
            lock (_stateGate) runtime.Status = runtime.Status with { Running = true, Status = "starting" };
            runtime.Task = Task.Run(async () =>
            {
                using var language = Localization.UseLanguage(project.Language);
                var status = "completed"; string? error = null;
                try
                {
                    if (sha is not null) await runtime.Runner.ReviewSingleCommitAsync(project, profile, sha, runtime.Cancellation.Token);
                    else
                    {
                        if (!runtime.Runner.Start(project, profile)) throw new InvalidOperationException("Cannot start review.");
                        while (runtime.Runner.IsRunning) await Task.Delay(250, runtime.Cancellation.Token);
                    }
                }
                catch (OperationCanceledException) { status = "canceled"; }
                catch (Exception e) { status = "failed"; error = GitService.SanitizeDiagnostic(e.Message); runtime.Journal.Append(error); }
                finally
                {
                    await runtime.Runner.StopAsync();
                    _jobs.Save(job with { Status = status, Error = error, FinishedAt = DateTimeOffset.UtcNow });
                    lock (_stateGate) runtime.Status = runtime.Status with { Running = false, Status = status, NextRun = null };
                    Changed?.Invoke(new(id, "finished", status, DateTimeOffset.UtcNow));
                }
            });
            return job;
        }
        finally { _commands.Release(); }
    }
    public async Task DeleteProjectAsync(string id, long revision)
    {
        await _commands.WaitAsync();
        try
        {
            if (revision != _jobs.Revision()) throw new InvalidOperationException("Settings changed in another browser. Reload before deleting.");
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Project not found.");
            if (runtime.Status.Running) throw new InvalidOperationException("Stop the project before deleting it.");
            var catalog = _configuration.LoadProjects();
            var index = catalog.Projects.FindIndex(p => p.ProjectId == id);
            if (index < 0) throw new KeyNotFoundException("Project not found.");
            catalog.Projects.RemoveAt(index);
            catalog.ActiveIndex = Math.Clamp(catalog.ActiveIndex > index ? catalog.ActiveIndex - 1 : catalog.ActiveIndex, 0, Math.Max(0, catalog.Projects.Count - 1));
            _jobs.AdvanceRevision(); _configuration.SaveProjects(catalog);
            lock (_stateGate) _runtimes.Remove(id);
            runtime.Cancellation?.Dispose();
            Changed?.Invoke(new(id, "project-deleted", "", DateTimeOffset.UtcNow));
        }
        finally { _commands.Release(); }
    }
    public async Task StopAsync(string id)
    {
        await _commands.WaitAsync();
        try
        {
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
            runtime.Cancellation?.Cancel();
            if (runtime.Task is not null) await runtime.Task;
        }
        finally { _commands.Release(); }
    }
    public async Task UpdateModelAsync(ModelProfile profile)
    {
        await _commands.WaitAsync();
        try { foreach (var runtime in _runtimes.Values) runtime.Runner.UpdateProfile(profile); }
        finally { _commands.Release(); }
    }
    public object Usage()
    {
        var usage = _usage.Snapshot(null, DateTimeOffset.Now);
        return new { today = usage.Today, total = usage.Total, error = _usage.Error };
    }
    public async Task SetStartCommitAsync(string id, string sha)
    {
        await _commands.WaitAsync();
        try
        {
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
            if (runtime.Status.Running) throw new InvalidOperationException("Stop the project first.");
            var settings = _configuration.LoadProjects().Projects.Single(p => p.ProjectId == id);
            var identity = await runtime.Git.GetRepositoryIdentityAsync(settings.RepositoryPath, default);
            var branch = await runtime.Git.ResolveBranchAsync(settings.RepositoryPath, settings.BranchRef, default);
            var commit = await runtime.Git.ResolveCommitAsync(settings.RepositoryPath, sha, default);
            var head = await runtime.Git.GetBranchHeadAsync(settings.RepositoryPath, branch, default);
            if (!await runtime.Git.IsAncestorAsync(settings.RepositoryPath, commit, head, default)) throw new ArgumentException("Commit is not in the selected branch.");
            await _state.SetStartCommitAsync(identity.CommonGitDirectory, branch, commit, default);
        }
        finally { _commands.Release(); }
    }
    public string Log(string id, bool details)
    {
        lock (_stateGate)
        {
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
            return (details ? runtime.Details : runtime.Journal).Snapshot(true) ?? "";
        }
    }
    public void ClearLog(string id, bool details)
    {
        lock (_stateGate)
        {
            var runtime = _runtimes.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
            (details ? runtime.Details : runtime.Journal).Clear();
        }
        Changed?.Invoke(new(id, "log-cleared", "", DateTimeOffset.UtcNow));
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _runtimes.Keys.ToArray()) await StopAsync(id);
        foreach (var runtime in _runtimes.Values) runtime.Cancellation?.Dispose();
    }
}
