using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KommitEye.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace KommitEye.Services;

public sealed class MailSettings
{
    public string Provider { get; set; } = "smtp";
    public string AccountId { get; set; } = "";
    public bool AuthorizationRequired { get; set; }
    public bool Enabled { get; set; }
    public bool SendToAuthor { get; set; }
    public bool SendWithoutBugs { get; set; }
    public string AttachmentFormat { get; set; } = "html";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public MailSettings Clone() => (MailSettings)MemberwiseClone();
    public void SetPassword(string password) => EncryptedPassword = password.Length == 0 ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public string GetPassword() => EncryptedPassword.Length == 0 ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedPassword), null, DataProtectionScope.CurrentUser));
    public List<MailboxAddress> Addresses() => Recipients.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(MailboxAddress.Parse).ToList();
    public void Validate()
    {
        if (AttachmentFormat is not ("html" or "md")) throw new InvalidOperationException("Attachment format must be html or md.");
        if (Provider is "gmail" or "outlook")
        {
            if (string.IsNullOrWhiteSpace(AccountId)) throw new MailAuthorizationException();
            if (From.Any(char.IsControl) || Recipients.Any(char.IsControl)) throw new InvalidOperationException("Invalid mail address.");
            _ = MailboxAddress.Parse(From);
            if (Addresses().Count == 0 && !SendToAuthor) throw new InvalidOperationException(Localization.Text("Enter a recipient.", "Укажите получателя."));
            return;
        }
        if (Provider != "smtp") throw new InvalidOperationException("Unsupported email provider.");
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Port is < 1 or > 65535)
            throw new InvalidOperationException(Localization.Text("Invalid SMTP host or port.", "Некорректный SMTP-сервер или порт."));
        if (Security is not ("None" or "StartTls" or "SslOnConnect")) throw new InvalidOperationException("Invalid SMTP security mode.");
        if (From.Any(char.IsControl) || Recipients.Any(char.IsControl) || Username.Any(char.IsControl))
            throw new InvalidOperationException("Invalid mail address or username.");
        _ = MailboxAddress.Parse(From);
        if (Addresses().Count == 0 && !SendToAuthor) throw new InvalidOperationException(Localization.Text("Enter a recipient.", "Укажите получателя."));
        _ = GetPassword();
    }
}

public sealed class MailJob
{
    public string Provider { get; set; } = "smtp";
    public string AccountId { get; set; } = "";
    public string AttachmentFormat { get; set; } = "html";
    public string Key { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Report { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public int Attempts { get; set; }
    public bool Sent { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
}

public sealed class MailNotifications : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IEmailProvider> _providers;
    public EmailAccount? GetAccount(string provider) => _providers.GetValueOrDefault(provider)?.Account;
    public string? GetStatusError(string provider) => _providers.GetValueOrDefault(provider)?.StatusError;
    public MailSettings GetSettings(string provider)
    {
        lock (_gate)
        {
            if (_settings.Provider == provider) return _settings.Clone();
            if (provider is not ("gmail" or "outlook" or "smtp")) throw new ArgumentException(nameof(provider));
            var path = _settingsPath + "." + provider;
            return File.Exists(path) ? JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException()
                : new MailSettings { Provider = provider };
        }
    }
    public EmailAccount? GoogleAccount => _providers.GetValueOrDefault("gmail")?.Account;
    public string? GoogleStatusError => _providers.GetValueOrDefault("gmail")?.StatusError;
    public int PausedCount { get { lock (_gate) return _jobs.Count(j => !j.Sent && !MatchesAccount(j, _settings)); } }
    private static bool MatchesAccount(MailJob job, MailSettings settings) => job.Provider == settings.Provider && job.AccountId == settings.AccountId;
    private readonly string _settingsPath, _queuePath;
    private readonly Action<string> _log;
    private readonly Func<MailSettings, MailJob, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly List<MailJob> _jobs;
    private readonly Task _worker;
    private MailSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    public string? LoadError { get; }
    public MailSettings Settings { get { lock (_gate) return _settings.Clone(); } }

    public MailNotifications(string directory, Action<string> log,
        Func<MailSettings, MailJob, CancellationToken, Task>? send = null, bool startWorker = true, Func<DateTimeOffset>? now = null, IEnumerable<IEmailProvider>? providers = null)
    {
        _providers = (providers ?? [new GmailEmailProvider(directory), new OutlookEmailProvider(directory)]).ToDictionary(p => p.Id, StringComparer.Ordinal);
        _settingsPath = Path.Combine(directory, "mail-settings.json");
        _queuePath = Path.Combine(directory, "mail-queue.json");
        // A UI/logging failure must never terminate the delivery worker.
        _log = message => { try { log(message); } catch { } };
        _send = send ?? ((settings, job, token) => job.Provider == "smtp" ? SendSmtpAsync(settings, job, token, _log) : _providers.TryGetValue(job.Provider, out var provider)
            ? provider.SendAsync(job, token) : throw new MailAuthorizationException());
        _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            _settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(_settingsPath))
                ?? throw new InvalidDataException() : new();
            _jobs = File.Exists(_queuePath) ? JsonSerializer.Deserialize<List<MailJob>>(File.ReadAllText(_queuePath))
                ?? throw new InvalidDataException() : [];
            if (_jobs.Any(j => j is null || string.IsNullOrWhiteSpace(j.Key) || j.Attempts < 0)) throw new InvalidDataException();
        }
        catch (Exception e)
        {
            _settings = new(); _jobs = [];
            LoadError = "Mail settings/queue could not be loaded (" + e.GetType().Name + "). Existing files are preserved.";
        }
        _worker = startWorker ? Task.Run(WorkerAsync) : Task.CompletedTask;
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public void SaveSettings(MailSettings settings, bool activate = true)
    {
        if (settings.Provider is "gmail" or "outlook")
        {
            var account = GetAccount(settings.Provider);
            if (account is null || settings.AccountId != account.Id) throw new MailAuthorizationException();
            settings.From = account.Address;
            settings.EncryptedPassword = "";
            settings.Username = "";
            settings.Host = "";
            settings.AuthorizationRequired = account.NeedsAuthorization;
        }
        if (settings.Enabled) settings.Validate();
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            if (!activate && _settings.Provider != settings.Provider)
            {
                Save(_settingsPath + "." + settings.Provider, settings);
                return;
            }
            Save(_settingsPath + "." + _settings.Provider, _settings);
            Save(_settingsPath, settings);
            _settings = settings.Clone();
        }
    }
    public Task ConnectGoogleAsync(string clientFile, CancellationToken token) => ConnectAsync("gmail", clientFile, token);
    public async Task ConnectAsync(string provider, string clientFile, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(3));
        await _sending.WaitAsync(linked.Token);
        try
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            var account = await _providers[provider].ConnectAsync(clientFile, linked.Token);
            var settings = GetSettings(provider);
            settings.Enabled = settings.Enabled && settings.Provider == provider && settings.AccountId == account.Id;
            settings.Provider = provider;
            settings.AccountId = account.Id;
            settings.From = account.Address;
            settings.AuthorizationRequired = false;
            SaveSettings(settings);
        }
        finally { _sending.Release(); }
    }

    public Task DisconnectGoogleAsync(CancellationToken token) => DisconnectAsync("gmail", token);
    public async Task DisconnectAsync(string provider, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _sending.WaitAsync(linked.Token);
        try
        {
            var settings = GetSettings(provider);
            settings.Enabled = false;
            settings.AuthorizationRequired = true;
            lock (_gate)
            {
                if (_settings.Provider == provider) { Save(_settingsPath, settings); _settings = settings; }
                else Save(_settingsPath + "." + provider, settings);
            }
            await _providers[provider].DisconnectAsync(linked.Token);
        }
        finally { _sending.Release(); }
    }

    public void Enqueue(CommitReviewed review)
    {
        if (review.HasUnstructuredResponse && !review.Failed) { _log("Mail: incomplete review skipped: " + review.Sha); return; }
        lock (_gate)
        {
            if (LoadError is not null) { _log(LoadError); return; }
            if (!_settings.Enabled) { _log("Mail: notifications disabled; skipped: " + review.Sha); return; }
            if (!review.Failed && (review.FindingCount == 0 || review.EmptyDiff) && !_settings.SendWithoutBugs)
            { _log("Mail: no findings; clean-review notifications disabled; skipped: " + review.Sha); return; }
            try
            {
                _settings.Validate();
                var recipients = _settings.Addresses();
                if (!review.Failed && _settings.SendToAuthor && !string.IsNullOrWhiteSpace(review.AuthorEmail))
                {
                    // Commit metadata is untrusted: accept exactly one bare mailbox, never a header/list.
                    var email = review.AuthorEmail.Trim();
                    if (!email.Any(char.IsControl) && MailboxAddress.TryParse(email, out var author) &&
                        author.Address.Equals(email, StringComparison.OrdinalIgnoreCase) && email.Contains('@'))
                    {
                        if (!recipients.Any(r => r.Address.Equals(author.Address, StringComparison.OrdinalIgnoreCase))) recipients.Add(author);
                    }
                    else _log("Mail: invalid commit author address skipped.");
                }
                if (recipients.Count == 0) { _log("Mail: no valid recipients for this commit; skipped."); return; }
                if (string.IsNullOrWhiteSpace(review.ReportMarkdown)) throw new InvalidDataException("Report is unavailable.");
                var identity = Path.GetFullPath(string.IsNullOrEmpty(review.RepositoryIdentity) ? review.RepositoryPath : review.RepositoryIdentity);
                if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + review.BranchRef + "\n" + review.Sha + (review.Failed ? "\nfailure" : ""))));
                if (_jobs.Any(j => j.Key == key)) { _log("Mail: this commit is already queued or sent: " + review.Sha); return; }
                var job = new MailJob { Key = key, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
                    Provider = _settings.Provider, AccountId = _settings.AccountId,
                    AttachmentFormat = _settings.AttachmentFormat,
                    Subject = Clean($"[KommitEye] {Path.GetFileName(review.RepositoryPath)} / {review.BranchRef} / {review.Sha[..Math.Min(8, review.Sha.Length)]} — " + (review.Failed ? "review failed" : $"bugs: {review.FindingCount}")),
                    Report = review.ReportMarkdown, From = _settings.From, Recipients = string.Join(";", recipients.Select(r => r.ToString())), NextAttempt = _now() };
                _jobs.Add(job);
                try { Save(_queuePath, _jobs); } catch { _jobs.Remove(job); throw; }
                _log(Localization.Text("Mail: report queued: ", "Почта: отчёт поставлен в очередь: ") + Describe(job));
            }
            catch (Exception e) { _log("Mail: cannot queue report (" + e.GetType().Name + "). Review is saved."); }
        }
    }
    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).Take(250).ToArray());
    private static string Describe(MailJob job) => $"id={Clean(job.MessageId)}; from={Clean(job.From)}; to={string.Join("; ", job.Recipients.Split(';').Select(Clean))}; subject={Clean(job.Subject)}";
    private static string FailureDetails(Exception exception) => exception is SmtpCommandException smtp
        ? $"{smtp.GetType().Name}; SMTP={(int)smtp.StatusCode}; reason={smtp.ErrorCode}; rejected-address={Clean(smtp.Mailbox?.Address ?? "-")}"
        : exception is MailProviderException or MailAuthorizationException ? exception.Message
        : exception is OperationCanceledException ? "Canceled or mail timeout" : exception.GetType().Name;

    private void MarkAuthorizationRequired(MailSettings failedSettings)
    {
        lock (_gate)
        {
            var saved = GetSettings(failedSettings.Provider);
            if (saved.AccountId != failedSettings.AccountId) return;
            saved.AuthorizationRequired = true;
            if (_settings.Provider == saved.Provider) { Save(_settingsPath, saved); _settings = saved; }
            else Save(_settingsPath + "." + saved.Provider, saved);
        }
    }

    public async Task TestAsync(MailSettings settings, CancellationToken token, bool saveSettings = true)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        token = linked.Token;
        settings.Validate();
        if (settings.Addresses().Count == 0) throw new InvalidOperationException("Enter an explicit recipient for the test email.");
        if (saveSettings) SaveSettings(settings);
        await _sending.WaitAsync(token);
        var job = new MailJob { MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
            Provider = settings.Provider, AccountId = settings.AccountId,
            AttachmentFormat = settings.AttachmentFormat,
            Subject = "KommitEye email test", Report = "Test message from KommitEye. No repository data is included.",
            From = settings.From, Recipients = settings.Recipients };
        try
        {
            _log("Mail: sending test; " + Describe(job));
            await _send(settings, job, token);
            _log("Mail: test accepted by mail provider (inbox delivery unconfirmed); " + Describe(job));
        }
        catch (MailAuthorizationException)
        {
            MarkAuthorizationRequired(settings);
            throw;
        }
        catch (Exception exception) { _log("Mail: test not confirmed; " + Describe(job) + "; " + FailureDetails(exception)); throw; }
        finally { _sending.Release(); }
    }
    public void RetryFailed()
    {
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            foreach (var job in _jobs.Where(j => !j.Sent && MatchesAccount(j, _settings) && j.Attempts >= 4))
            { job.Attempts = 0; job.NextAttempt = _now(); }
            Save(_queuePath, _jobs);
        }
    }
    public (int Pending, int Failed) Counts()
    { lock (_gate) return (_jobs.Count(j => !j.Sent && j.Attempts < 4), _jobs.Count(j => !j.Sent && MatchesAccount(j, _settings) && j.Attempts >= 4)); }

    private async Task WorkerAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) { _log("Mail queue error (" + e.GetType().Name + ")."); }
            try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task ProcessOnceAsync(CancellationToken token)
    {
        await _sending.WaitAsync(token);
        try
        {
            MailJob? job;
            MailSettings settings;
            lock (_gate)
            {
                if (LoadError is not null || !_settings.Enabled || _settings.AuthorizationRequired) return;
                job = _jobs.FirstOrDefault(j => !j.Sent && MatchesAccount(j, _settings) && j.Attempts < 4 && j.NextAttempt <= _now());
                if (job is null) return;
                settings = _settings.Clone();
                job.Attempts++;
                job.NextAttempt = _now().AddMinutes(job.Attempts switch { 1 => 1, 2 => 5, _ => 15 });
                Save(_queuePath, _jobs); // Persist attempts before touching the network.
            }
            try
            {
                _log($"Mail: sending attempt {job.Attempts}/4 via {Clean(settings.Provider)}; " + Describe(job));
                await _send(settings, job, token);
                lock (_gate) { job.Sent = true; job.Report = ""; Save(_queuePath, _jobs); }
                _log(Localization.Text("Mail: accepted by mail provider (inbox delivery unconfirmed); ", "Почта: принято почтовым сервисом (доставка в ящик не подтверждена); ") + Describe(job));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { _log("Mail: sending canceled, acceptance unknown; " + Describe(job)); throw; }
            catch (MailAuthorizationException)
            {
                lock (_gate)
                {
                    job.Attempts = Math.Max(0, job.Attempts - 1);
                    MarkAuthorizationRequired(settings);
                    Save(_queuePath, _jobs);
                }
                _log(Localization.Text("Mail paused: reconnect your email account. Queued reports are preserved.", "Отправка приостановлена: подключите почтовый аккаунт заново. Очередь сохранена."));
            }
            catch (Exception e)
            {
                // Never include server text or credentials in logs.
                _log($"Mail: attempt {job.Attempts}/4 not confirmed ({FailureDetails(e)}); {Describe(job)}; " +
                    (job.Attempts >= 4 ? "Retries exhausted. Use Retry failed mail." : $"Next retry: {job.NextAttempt.LocalDateTime:yyyy-MM-dd HH:mm:ss}."));
            }
        }
        finally { _sending.Release(); }
    }

    public static async Task SendSmtpAsync(MailSettings settings, MailJob job, CancellationToken token, Action<string>? log = null)
    {
        settings.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new SmtpClient { Timeout = 30000 };
        log?.Invoke($"Mail: connecting to {Clean(settings.Host)}:{settings.Port}; security={settings.Security}; id={Clean(job.MessageId)}");
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security switch
        { "None" => SecureSocketOptions.None, "SslOnConnect" => SecureSocketOptions.SslOnConnect, _ => SecureSocketOptions.StartTls }, timeout.Token);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            log?.Invoke("Mail: authenticating; id=" + Clean(job.MessageId));
            await client.AuthenticateAsync(settings.Username, settings.GetPassword(), timeout.Token);
        }
        log?.Invoke("Mail: submitting message; " + Describe(job));
        using var message = CreateMessage(job);
        await client.SendAsync(message, timeout.Token);
        // Once accepted, a disconnect failure must not schedule a duplicate.
        try { await client.DisconnectAsync(true, timeout.Token); } catch { }
    }

    public static MimeMessage CreateMessage(MailJob job)
    {
        var message = new MimeMessage { Subject = job.Subject, MessageId = job.MessageId, Date = DateTimeOffset.Now };
        message.From.Add(MailboxAddress.Parse(job.From));
        foreach (var address in job.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(MailboxAddress.Parse(address));
        var html = RenderHtml(job.Report);
        var body = new BodyBuilder { TextBody = job.Report, HtmlBody = html };
        if (job.AttachmentFormat is not ("html" or "md")) throw new InvalidOperationException("Unsupported queued attachment format.");
        var markdown = job.AttachmentFormat == "md";
        body.Attachments.Add(markdown ? "commit-review.md" : "commit-review.html",
            Encoding.UTF8.GetBytes(markdown ? job.Report : html), new ContentType("text", markdown ? "markdown" : "html"));
        message.Body = body.ToMessageBody();
        return message;
    }

    public static string RenderHtml(string report)
    {
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#202124;line-height:1.5;max-width:960px;margin:24px\">");
        var fenced = false;
        foreach (var raw in report.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                html.Append(fenced ? "</code></pre>" : "<pre style=\"white-space:pre-wrap;background:#f3f5f7;padding:12px\"><code>");
                fenced = !fenced;
                continue;
            }
            if (!fenced && raw.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var encoded = System.Net.WebUtility.HtmlEncode(raw);
            if (fenced) { html.Append(encoded).Append('\n'); continue; }
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var level = raw.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 3 && raw.Length > level && raw[level] == ' ')
                html.Append($"<h{level}>").Append(System.Net.WebUtility.HtmlEncode(raw[(level + 1)..].Replace("`", ""))).Append($"</h{level}>");
            else html.Append("<p style=\"margin:6px 0;white-space:pre-wrap\">").Append(encoded).Append("</p>");
        }
        if (fenced) html.Append("</code></pre>");
        return html.Append("</body></html>").ToString();
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _worker;
        await _sending.WaitAsync();
        _sending.Release();
        _stop.Dispose();
    }
}
