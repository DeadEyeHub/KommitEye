using KommitEye.Models;

namespace KommitEye.Services;

public sealed class ReviewRunner
{
    private readonly GitService _git;
    private readonly ModelClient _model;
    private readonly ConfigurationStore _configuration;
    private readonly StateStore _stateStore;
    private readonly ReportWriter _reportWriter;
    private readonly FailureDiagnostics _failureDiagnostics = new(AppPaths.DataDirectory);
    private AppSettings? _diagnosticSettings;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private Task? _stopTask;
    private bool _manualReviewActive;
    private string _activeCommit = string.Empty;
    private int _remaining;
    private string? _stopReason;
    private AppSettings? _updatedSettings;
    private ModelProfile? _updatedProfile;
    private TaskCompletionSource _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void UpdateSettings(AppSettings settings)
    {
        var schedule = new DailyModelSchedule(settings);
        lock (_lifecycleLock)
        {
            _updatedSettings = settings;
            _schedule = schedule;
            _settingsChanged.TrySetResult();
        }
    }
    public void UpdateProfile(ModelProfile profile)
    {
        profile.Parameters.Validate();
        lock (_lifecycleLock) { _updatedProfile = profile.Clone(); _settingsChanged.TrySetResult(); }
    }
    private DailyModelSchedule _schedule = new(new AppSettings());
    private readonly Func<DateTime> _localNow;
    public event Action<int?>? RemainingChanged;
    public event Action<DateTimeOffset?>? NextRunChanged;
    public event Action<(string Repository, string Sha, TokenUsage? Usage)>? UsageReceived;
    private void SetRemaining(int count) { _remaining = count; Publish(RemainingChanged, (int?)count); }

    public event Action<(string Kind, string Detail)>? OperationalEvent;
    public event Action<string>? Log;
    public event Action<string>? ModelLog;
    public event Action<string>? StatusChanged;
    public event Action? AutomaticStopped;
    public event Action<string>? CommitChanged;
    public event Action<CommitInfo>? CommitInfoChanged;
    public event Action<ReviewProgress>? Progress;
    public event Action<CommitReviewed>? Reviewed;
    public string? FailureReason => _stopReason;
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
                return _runTask is { IsCompleted: false };
        }
    }

    public ReviewRunner(
        GitService git,
        ModelClient model,
        ConfigurationStore configuration,
        StateStore stateStore,
        ReportWriter reportWriter,
        Func<DateTime>? localNow = null)
    {
        _git = git;
        _model = model;
        _configuration = configuration;
        _stateStore = stateStore;
        _reportWriter = reportWriter;
        _localNow = localNow ?? (() => DateTime.Now);
        Log += message => _failureDiagnostics.Append("journal", message);
        ModelLog += message => _failureDiagnostics.Append("model", message);
        _git.Diagnostic += message => _failureDiagnostics.Append("git", message);
    }

    public bool Start(AppSettings settings, ModelProfile profile)
    {
        lock (_lifecycleLock)
        {
            if (_runTask is { IsCompleted: false } || _stopTask is not null || _manualReviewActive)
                return false;

            _updatedSettings = null; _updatedProfile = null;
            _stopReason = null;
            _diagnosticSettings = settings;
            _failureDiagnostics.Clear();
            _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.Parameters.Validate();
            _schedule = new DailyModelSchedule(settings);
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _runTask = RunMonitoredAsync(settings, profile.Clone(), _cancellation.Token);
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;
            if (_cancellation is null || _runTask is null)
                return Task.CompletedTask;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            _cancellation.Cancel();
            _ = FinishStopAsync(_cancellation, _runTask, completion);
            return completion.Task;
        }
    }

    public async Task<ReviewResult> ReviewSingleCommitAsync(
        AppSettings settings,
        ModelProfile profile,
        string revision,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_runTask is { IsCompleted: false } || _stopTask is not null || _manualReviewActive)
                throw new InvalidOperationException(Localization.Text(
                    "Stop automatic monitoring before reviewing a selected commit.",
                    "Остановите автоматическую проверку перед проверкой выбранного коммита."));
            _manualReviewActive = true;
            _diagnosticSettings = settings;
            _failureDiagnostics.Clear();
        }

        try
        {
            _schedule = new DailyModelSchedule(settings);
            _activeCommit = string.Empty;
            _schedule.Check(_localNow());
            var repositoryPath = Path.GetFullPath(settings.RepositoryPath);
            await _git.ValidateRepositoryAsync(repositoryPath, cancellationToken);
            var identity = await _git.GetRepositoryIdentityAsync(repositoryPath, cancellationToken);
            repositoryPath = identity.WorkTreeRoot;
            var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
            var sha = await _git.ResolveCommitAsync(repositoryPath, revision, cancellationToken);
            SetRemaining(1);
            var result = await AnalyzeAndReportAsync(
                repositoryPath, identity.CommonGitDirectory, branch, sha, profile.Clone(), true, cancellationToken);
            Complete(repositoryPath, branch, sha, profile, result, true, cancellationToken);
            StatusChanged?.Invoke(Localization.Text("Manual review completed", "Ручная проверка завершена"));
            Log?.Invoke(Localization.Format(
                "Selected commit {0} reviewed, findings: {1}.",
                "Выбранный коммит {0} проверен, замечаний: {1}.",
                Short(sha), result.Findings.Count));
            return result;
        }
        catch (ModelSchedulePauseException)
        {
            Emit(ReviewStage.ScheduledPause, profile, _activeCommit);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Emit(ReviewStage.Canceled, profile, _activeCommit);
            Publish(OperationalEvent, ("Review stopped", "Manual review canceled by user or application shutdown."));
            throw;
        }
        catch (Exception exception)
        {
            Emit(ReviewStage.Failed, profile, _activeCommit, exception.Message);
            SaveFailureDiagnostics(profile, exception.ToString(), true);
            throw;
        }
        finally
        {
            lock (_lifecycleLock)
                _manualReviewActive = false;
        }
    }

    private async Task FinishStopAsync(
        CancellationTokenSource cancellation,
        Task runTask,
        TaskCompletionSource completion)
    {
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log?.Invoke(Localization.Format(
                "Error while stopping: {0}",
                "Ошибка при остановке: {0}",
                exception.Message));
        }
        finally
        {
            cancellation.Dispose();
            lock (_lifecycleLock)
            {
                _cancellation = null;
                _runTask = null;
                _stopTask = null;
            }
            StatusChanged?.Invoke(Localization.Text("Stopped", "Остановлено"));
            Publish(NextRunChanged, (DateTimeOffset?)null);
            Publish(RemainingChanged, (int?)null);
            completion.SetResult();
        }
    }

    private async Task RunMonitoredAsync(AppSettings settings, ModelProfile profile, CancellationToken token)
    {
        try { await RunLoopAsync(settings, profile, token); }
        finally { Publish(OperationalEvent, ("Project stopped", _stopReason ?? (token.IsCancellationRequested ? "Monitoring stopped by user or application shutdown." : "Monitoring stopped after errors. See the project journal."))); }
    }

    private async Task RunLoopAsync(AppSettings settings, ModelProfile profile, CancellationToken cancellationToken)
    {
        Log?.Invoke(Localization.Text("Review started.", "Проверка запущена."));
        while (!cancellationToken.IsCancellationRequested)
        {
            Task configurationChanged;
            lock (_lifecycleLock)
            {
                settings = _updatedSettings ?? settings;
                _diagnosticSettings = settings;
                profile = _updatedProfile?.Clone() ?? profile;
                _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                configurationChanged = _settingsChanged.Task;
            }
            try
            {
                await WaitForScheduleAsync(profile, cancellationToken);
                _activeCommit = string.Empty;
                Emit(ReviewStage.CheckingRepository, profile, _activeCommit);
                Publish(RemainingChanged, (int?)null);
                await RunCycleAsync(settings, profile, cancellationToken);
                Emit(ReviewStage.WaitingForChanges, profile, _activeCommit);
                StatusChanged?.Invoke(Localization.Format(
                    "Waiting {0} sec.",
                    "Ожидание {0} сек.",
                    settings.PollIntervalSeconds));
            }
            catch (ModelSchedulePauseException exception)
            {
                Emit(ReviewStage.ScheduledPause, profile, _activeCommit);
                Publish(Log, exception.Message);
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Emit(ReviewStage.Canceled, profile, _activeCommit);
                throw;
            }
            catch (Exception exception)
            {
                Emit(ReviewStage.Failed, profile, _activeCommit, exception.Message);
                SaveFailureDiagnostics(profile, exception.ToString(), false);
                StatusChanged?.Invoke(Localization.Text("Error", "Ошибка"));
                Log?.Invoke(Localization.Format("Error: {0}", "Ошибка: {0}", exception.Message));
                _stopReason = Localization.Format(
                    "Project stopped at commit {0}: {1}. Fix the error and press Start to retry.",
                    "Проект остановлен на коммите {0}: {1}. Исправьте ошибку и нажмите «Старт» для повторной проверки.",
                    string.IsNullOrEmpty(_activeCommit) ? "—" : _activeCommit, GitService.SanitizeDiagnostic(exception.Message));
                Publish(Log, _stopReason);
                Publish(StatusChanged, _stopReason);
                Publish(NextRunChanged, (DateTimeOffset?)null);
                Publish(RemainingChanged, (int?)null);
                AutomaticStopped?.Invoke();
                return;
            }

            Publish(NextRunChanged, (DateTimeOffset?)DateTimeOffset.Now.AddSeconds(settings.PollIntervalSeconds));
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), wait.Token), configurationChanged);
                wait.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { Publish(NextRunChanged, (DateTimeOffset?)null); }
        }
    }

    private async Task RunCycleAsync(
        AppSettings settings,
        ModelProfile profile,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.GetFullPath(settings.RepositoryPath);
        await _git.ValidateRepositoryAsync(repositoryPath, cancellationToken);
        var identity = await _git.GetRepositoryIdentityAsync(repositoryPath, cancellationToken);
        repositoryPath = identity.WorkTreeRoot;
        var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
        await _stateStore.RegisterRepositoryAsync(
            identity.CommonGitDirectory, repositoryPath, cancellationToken);
        var position = await _stateStore.GetReviewPositionAsync(
            identity.CommonGitDirectory, branch, cancellationToken);
        var previousCommit = position.Cursor;
        string? fetchedTarget = null;

        if (settings.PullEnabled)
        {
            StatusChanged?.Invoke(Localization.Text("Running git fetch", "Выполняется git fetch"));
            var fetchSettings = new AppSettings
            {
                BranchRef = branch,
                FetchSubmodules = settings.FetchSubmodules,
                FetchSubmodulesAlways = settings.FetchSubmodulesAlways,
                GitAuthenticationMode = settings.GitAuthenticationMode,
                SshPrivateKeyPath = settings.SshPrivateKeyPath,
                PlinkPath = settings.PlinkPath
            };
            var fetch = await _git.FetchAsync(repositoryPath, fetchSettings, cancellationToken,
                message => Publish(Log, message));
            Publish(ModelLog, GitService.SanitizeDiagnostic($"Git fetch exit code: {fetch.ExitCode}\nstdout: {fetch.Output}\nstderr: {fetch.Error}"));
            if (fetch.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(fetch.Error) ? fetch.Output : fetch.Error;
                if (string.IsNullOrWhiteSpace(details))
                    details = Localization.Text("Git returned no error output.", "Git не вернул текст ошибки.");
                throw new GitException(Localization.Format(
                    "Git fetch failed with exit code {0}: {1}",
                    "Git fetch не выполнен, код завершения {0}: {1}",
                    fetch.ExitCode,
                    GitService.SanitizeDiagnostic(details.Trim())));
            }
            fetchedTarget = fetch.FetchedCommit;
        }

        if (fetchedTarget is not null && branch.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var localHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            var advancing = await _git.GetAdvanceCommitsAsync(repositoryPath, localHead, fetchedTarget, cancellationToken);
            if (advancing.Count > 0)
                await _git.ValidateAdvanceAsync(repositoryPath, branch, localHead, cancellationToken);
            var replay = new List<string>();
            if (position.PendingStart is not null)
            {
                if (!await _git.IsAncestorAsync(repositoryPath, position.PendingStart, localHead, cancellationToken))
                    throw new GitException("Selected start commit must be an ancestor of the local branch tip.");
                replay.Add(position.PendingStart);
                replay.AddRange(await _git.GetCommitsAfterAsync(repositoryPath, position.PendingStart, localHead, cancellationToken));
            }
            SetRemaining(replay.Count + advancing.Count);
            foreach (var oldSha in replay)
                await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, oldSha, profile, cancellationToken);
            Publish(Log, Localization.Format("Local baseline {0}; fetched tip {1}; commits to review and advance: {2}.",
                "Локальная точка старта {0}; полученная вершина {1}; коммитов для проверки и продвижения: {2}.", Short(localHead), Short(fetchedTarget), advancing.Count));
            foreach (var nextSha in advancing)
            {
                await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, nextSha, profile, cancellationToken, localHead, settings.UpdateSubmodulesAfterAdvance);
                localHead = nextSha;
            }
            return;
        }

        if (position.PendingStart is not null)
        {
            var pendingHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            if (!await _git.IsAncestorAsync(repositoryPath, position.PendingStart, pendingHead, cancellationToken))
                throw new GitException(Localization.Text(
                    "The selected start commit is not an ancestor of the selected branch tip.",
                    "Выбранный стартовый коммит не является предком вершины выбранной ветки."));
            Log?.Invoke(Localization.Format(
                "Starting automatic review with selected commit {0}.",
                "Автоматическая проверка начинается с выбранного коммита {0}.",
                Short(position.PendingStart)));
            var pendingCommits = await _git.GetCommitsAfterAsync(repositoryPath, position.PendingStart, pendingHead, cancellationToken);
            SetRemaining(1 + pendingCommits.Count);
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, position.PendingStart, profile, cancellationToken);
            foreach (var pendingSha in pendingCommits)
                await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, pendingSha, profile, cancellationToken);
            return;
        }
        else if (previousCommit is null)
        {
            var initialHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            SetRemaining(1);
            Log?.Invoke(Localization.Format(
                "First connection: reviewing current commit {0}.",
                "Первое подключение: проверяется текущий коммит {0}.",
                Short(initialHead)));
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, initialHead, profile, cancellationToken);
            previousCommit = initialHead;
        }

        var head = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
        if (head.Equals(previousCommit, StringComparison.Ordinal))
        {
            SetRemaining(0);
            Publish(Log, Localization.Format("No new commits on {0}; tip {1}.", "Новых коммитов в {0} нет; вершина {1}.", branch, Short(head)));
            return;
        }

        if (!await _git.IsAncestorAsync(repositoryPath, previousCommit, head, cancellationToken))
            throw new GitException(Localization.Text(
                "History changed: the saved commit is not an ancestor of the selected branch tip.",
                "История изменена: сохраненный коммит не является предком вершины выбранной ветки."));

        var commits = await _git.GetCommitsAfterAsync(repositoryPath, previousCommit, head, cancellationToken);
        SetRemaining(commits.Count);
        Publish(Log, Localization.Format("Found {0} new commits on {1}.", "Найдено новых коммитов: {0}, ветка {1}.", commits.Count, branch));
        foreach (var sha in commits)
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, sha, profile, cancellationToken);
    }

    private async Task ProcessCommitAsync(
        string repositoryPath,
        string repositoryIdentity,
        string branch,
        string sha,
        ModelProfile profile,
        CancellationToken cancellationToken,
        string? advanceFrom = null,
        bool updateSubmodulesAfterAdvance = true)
    {
        lock (_lifecycleLock) if (!_manualReviewActive) profile = _updatedProfile?.Clone() ?? profile;
        var result = await AnalyzeAndReportAsync(
            repositoryPath, repositoryIdentity, branch, sha, profile, false, cancellationToken);
        if (advanceFrom is not null)
        {
            await _git.AdvanceBranchAsync(repositoryPath, branch, advanceFrom, sha, cancellationToken, updateSubmodulesAfterAdvance);
            if (!updateSubmodulesAfterAdvance)
                Publish(Log, Localization.Text("Submodule checkout skipped by project settings; working copies retain their current SHAs.", "Обновление сабмодулей отключено в настройках проекта; их рабочие копии остались на прежних SHA."));
            Publish(Log, Localization.Format("Local branch {0} advanced: {1} → {2}.",
                "Локальная ветка {0} продвинута: {1} → {2}.", branch, Short(advanceFrom), Short(sha)));
        }
        Emit(ReviewStage.SavingCursor, profile, sha);
        await _stateStore.SetCursorAsync(repositoryIdentity, branch, sha, cancellationToken);
        if (result.Failure is not null)
        {
            SetRemaining(Math.Max(0, _remaining - 1));
            Emit(ReviewStage.Failed, profile, sha, result.Failure);
            Publish(Log, Localization.Format("Commit {0} skipped; failure saved in report. Continuing.",
                "Коммит {0} пропущен; причина сохранена в отчёте. Продолжаем.", Short(sha)));
            return;
        }
        Complete(repositoryPath, branch, sha, profile, result, false, cancellationToken);
        Log?.Invoke(Localization.Format(
            "Commit {0} reviewed, findings: {1}.",
            "Коммит {0} проверен, замечаний: {1}.",
            Short(sha), result.Findings.Count));
    }

    private async Task<ReviewResult> AnalyzeAndReportAsync(
        string repositoryPath,
        string repositoryIdentity,
        string branch,
        string sha,
        ModelProfile profile,
        bool manualReview,
        CancellationToken cancellationToken)
    {
        _activeCommit = sha;
        _schedule.Check(_localNow());
        StatusChanged?.Invoke(Localization.Format("Reviewing {0}", "Проверяется {0}", Short(sha)));
        CommitChanged?.Invoke(Short(sha));
        Emit(ReviewStage.Started, profile, sha);

        var analysisClock = System.Diagnostics.Stopwatch.StartNew();

        var commit = await _git.GetCommitInfoAsync(repositoryPath, sha, cancellationToken);
        Publish(CommitInfoChanged, commit);
        ReviewResult result;
        try
        {
            result = await AnalyzeAsync(repositoryPath, branch, sha, profile, repositoryIdentity, cancellationToken);
        }
        catch (ModelSchedulePauseException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manualReview || exception is not ReviewApiException { IsTransient: true })
            {
                var failed = new ReviewResult { Failure = GitService.SanitizeDiagnostic(exception.Message), AnalysisDuration = analysisClock.Elapsed };
                try
                {
                    await _reportWriter.AppendAsync(repositoryPath, branch, commit, failed, manualReview, cancellationToken, repositoryIdentity);
                    PublishFailedReview(repositoryPath, branch, sha, failed, manualReview);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception saveError) { Publish(Log, GitService.SanitizeDiagnostic($"Could not save failure report: {saveError.Message}")); }
                throw;
            }
            Emit(ReviewStage.Failed, profile, sha, exception.Message);
            SaveFailureDiagnostics(profile, exception.ToString(), manualReview);
            result = new ReviewResult { Failure = GitService.SanitizeDiagnostic(exception.Message) };
        }
        Emit(ReviewStage.Report, profile, sha);
        analysisClock.Stop();
        result.AnalysisDuration = analysisClock.Elapsed;
        await _reportWriter.AppendAsync(
            repositoryPath, branch, commit, result, manualReview, cancellationToken, repositoryIdentity);
        if (result.Failure is not null) PublishFailedReview(repositoryPath, branch, sha, result, manualReview);
        return result;
    }

    private void PublishFailedReview(string path, string branch, string sha, ReviewResult result, bool manual) =>
        Publish(Reviewed, new CommitReviewed(path, branch, sha, 0, false, false, manual,
            result.ReportMarkdown, result.RepositoryIdentity, result.AuthorEmail, Failed: true));

    private async Task<ReviewResult> AnalyzeAsync(string repositoryPath, string branch, string sha,
        ModelProfile profile, string repositoryIdentity, CancellationToken cancellationToken)
    {
        Emit(ReviewStage.PreparingDiff, profile, sha);
        using var tools = await GitToolSession.CreateAsync(repositoryPath, sha, cancellationToken, profile.Parameters.SnapshotMb);
        var result = new ReviewResult { EmptyDiff = await tools.IsEmptyAsync(cancellationToken) };
        if (!result.EmptyDiff)
        {
            var response = await _model.ReviewAsync(profile, tools, branch, _configuration.LoadPrompt(), cancellationToken,
                stage => Emit(stage, profile, sha, stage == ReviewStage.AgentStarted
                    ? profile.Parameters.ReviewMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), message =>
                    Publish(ModelLog, message), (stage, detail) => Emit(stage, profile, sha, detail),
                usage => Publish(UsageReceived, (repositoryIdentity, sha, usage)),
                beforeRequest: () => _schedule.Check(_localNow()));
            Emit(ReviewStage.Parsing, profile, sha);
            result = ReviewParser.Parse(response);
        }

        return result;
    }

    private async Task WaitForScheduleAsync(ModelProfile profile, CancellationToken token)
    {
        if (!_schedule.IsBlocked(_localNow())) return;
        Emit(ReviewStage.ScheduledPause, profile, _activeCommit);
        Publish(Log, Localization.Text("Model paused by daily schedule.", "Модель приостановлена по ежедневному расписанию."));
        try
        {
            while (_schedule.IsBlocked(_localNow()))
            {
                var resume = _schedule.ResumeAt(_localNow());
                Publish(StatusChanged, Localization.Format("Scheduled pause until {0:HH:mm}", "Пауза по расписанию до {0:HH:mm}", resume));
                Publish(NextRunChanged, (DateTimeOffset?)new DateTimeOffset(resume));
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        finally { Publish(NextRunChanged, (DateTimeOffset?)null); }
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    private void Emit(ReviewStage stage, ModelProfile profile, string sha, string detail = "")
    {
        _failureDiagnostics.Append("stage", $"{stage} | {sha} | {detail}");
        Publish(Progress, new ReviewProgress(stage, profile.Model, sha, detail));
        if (stage is ReviewStage.Failed or ReviewStage.ModelUnavailable)
            Publish(OperationalEvent, ("Project error", GitService.SanitizeDiagnostic(detail)));
    }

    private void SaveFailureDiagnostics(ModelProfile profile, string error, bool manual)
    {
        if (_diagnosticSettings is null) return;
        try
        {
            var id = _failureDiagnostics.Save(_diagnosticSettings, profile, _activeCommit, error, manual);
            Publish(Log, $"Failure diagnostics #{id} saved: {_failureDiagnostics.DatabasePath}");
        }
        catch (Exception exception)
        {
            // A storage failure must not hide the original review failure or resume the project.
            Publish(Log, $"Could not save failure diagnostics to '{_failureDiagnostics.DatabasePath}': {GitService.SanitizeDiagnostic(exception.Message)}");
        }
    }

    private void Complete(string path, string branch, string sha, ModelProfile profile,
        ReviewResult result, bool manual, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetRemaining(Math.Max(0, _remaining - 1));
        Emit(ReviewStage.Completed, profile, sha);
        Publish(Reviewed, new CommitReviewed(path, branch, sha, result.Findings.Count,
            result.UnstructuredResponse is not null, result.EmptyDiff, manual,
            result.ReportMarkdown, result.RepositoryIdentity, result.AuthorEmail));
    }

    private static void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            // Observers must not turn a persisted review into a failed operation.
            try { handler(value); }
            catch { }
        }
    }
}
