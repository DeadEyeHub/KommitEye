namespace KommitEye.Services;

public sealed record EmailAccount(string Provider, string Id, string Address, bool NeedsAuthorization);

public interface IEmailProvider
{
    string Id { get; }
    EmailAccount? Account { get; }
    string? StatusError { get; }
    Task<EmailAccount> ConnectAsync(string clientFile, CancellationToken token);
    Task DisconnectAsync(CancellationToken token);
    Task SendAsync(MailJob job, CancellationToken token);
}

public sealed class MailAuthorizationException : Exception
{
    public MailAuthorizationException() : base(Localization.Text(
        "Google authorization is required. Reconnect your account and allow email sending.",
        "Требуется авторизация Google. Подключите аккаунт заново и разрешите отправку писем.")) { }
}

public sealed class MailProviderException(string message) : Exception(message);
