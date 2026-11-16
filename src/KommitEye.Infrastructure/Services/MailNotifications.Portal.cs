using MimeKit;

namespace KommitEye.Services;

public sealed partial class MailNotifications
{
    public bool AuthorPortalEnabled { get; set; }

    public bool EnqueuePortal(string key, string recipients, string subject, string body, string url)
    {
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            if (!_settings.Enabled) return false;
            if (_jobs.Any(j => j.Key == key)) return true;
            _settings.Validate(_protector);
            var addresses = recipients.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (addresses.Length == 0) return false;
            foreach (var address in addresses) _ = MailboxAddress.Parse(address);
            var job = new MailJob { Key = key, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
                Provider = _settings.Provider, AccountId = _settings.AccountId, From = _settings.From,
                Recipients = recipients, Subject = Clean(subject), Report = body + "\n\n" + url,
                ActionUrl = url, NoAttachment = true, NextAttempt = _now() };
            _jobs.Add(job);
            try { Save(_queuePath, _jobs); } catch { _jobs.Remove(job); throw; }
            return true;
        }
    }

    // Login codes bypass the report queue and are never persisted or included in mail logs.
    public async Task SendAuthorCodeAsync(string email, string code, DateTimeOffset expires, CancellationToken token)
    {
        MailSettings settings;
        lock (_gate)
        {
            if (LoadError is not null || !_settings.Enabled || _settings.AuthorizationRequired)
                throw new InvalidOperationException("Email sign-in is unavailable. Contact the administrator.");
            settings = _settings.Clone();
        }
        settings.Validate(_protector);
        if (_now() >= expires) throw new InvalidOperationException("Code expired. Request another code.");
        var job = new MailJob { Provider = settings.Provider, AccountId = settings.AccountId, From = settings.From,
            Recipients = email, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(), NoAttachment = true,
            Subject = "[KommitEye] Код входа / Sign-in code",
            Report = $"Код входа / Sign-in code: {code}\n\nДействует до / Valid until: {expires:O}\nНе сообщайте код другим. / Do not share this code." };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _send(settings, job, timeout.Token);
    }
}
