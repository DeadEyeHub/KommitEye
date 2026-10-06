using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace GitReviewer.Services;

public sealed class GmailEmailProvider : IEmailProvider
{
    private readonly OAuthVault _vault;
    private readonly IGoogleOAuthClient _oauth;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleMailSession? _session;
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public string Id => "gmail";
    public EmailAccount? Account => _session is { } s ? new(Id, s.AccountId, s.Email, s.NeedsAuthorization) : null;
    public string? StatusError { get; private set; }

    public GmailEmailProvider(string directory, IGoogleOAuthClient? oauth = null, HttpClient? http = null)
    {
        _vault = new OAuthVault(Path.Combine(directory, "oauth", "gmail-account.bin"));
        _oauth = oauth ?? new GoogleOAuthClient();
        _http = http ?? DefaultHttp;
        try
        {
            _session = _vault.Load<GoogleMailSession>();
            if (_session is { } s && (string.IsNullOrWhiteSpace(s.AccountId) || string.IsNullOrWhiteSpace(s.Email) ||
                string.IsNullOrWhiteSpace(s.Client.ClientId) || string.IsNullOrWhiteSpace(s.Token.RefreshToken)))
                throw new InvalidDataException();
        }
        catch
        {
            _session = null;
            StatusError = Localization.Text("Saved Google authorization cannot be read. Reconnect your account.", "Не удалось прочитать сохранённую авторизацию Google. Подключите аккаунт заново.");
        }
    }

    public async Task<EmailAccount> ConnectAsync(string clientFile, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ClientSecrets client;
            if (string.IsNullOrWhiteSpace(clientFile) && _session is not null) client = _session.Client;
            else
            {
                using var input = JsonDocument.Parse(await File.ReadAllTextAsync(clientFile, token));
                if (!input.RootElement.TryGetProperty("installed", out var desktop))
                    throw new MailProviderException(Localization.Text("Choose an OAuth JSON for a Desktop app.", "Выберите OAuth JSON типа Desktop app."));
                client = new ClientSecrets { ClientId = desktop.GetProperty("client_id").GetString(), ClientSecret = desktop.GetProperty("client_secret").GetString() };
                if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
                    throw new MailProviderException(Localization.Text("OAuth client ID or secret is missing.", "В OAuth JSON отсутствует client ID или secret."));
            }
            var session = await _oauth.ConnectAsync(client, token);
            token.ThrowIfCancellationRequested();
            _vault.Save(session);
            _session = session;
            StatusError = null;
            return Account!;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var old = _session;
            _vault.Delete();
            _session = null;
            StatusError = null;
            if (old is not null)
            {
                try { await _oauth.RevokeAsync(old, token); }
                catch
                {
                    throw new MailProviderException(Localization.Text(
                        "Disconnected locally. Google revocation could not be confirmed; remove GitReviewer access at myaccount.google.com/permissions if needed.",
                        "Аккаунт отключён локально. Отзыв доступа у Google не подтверждён; при необходимости удалите доступ GitReviewer на myaccount.google.com/permissions."));
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SendAsync(MailJob job, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var session = _session ?? throw new MailAuthorizationException();
            if (session.NeedsAuthorization) throw new MailAuthorizationException();
            if (job.Provider != Id || job.AccountId != session.AccountId || !job.From.Equals(session.Email, StringComparison.OrdinalIgnoreCase))
                throw new MailProviderException(Localization.Text("Queued message belongs to another mailbox; sending is blocked.", "Письмо в очереди принадлежит другому ящику; отправка заблокирована."));
            using var message = MailNotifications.CreateMessage(job);
            using var bytes = new MemoryStream();
            await message.WriteToAsync(bytes, token);
            var raw = Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var accessToken = await _oauth.AccessTokenAsync(session, attempt > 0, token);
                _vault.Save(session); // Persist refreshed/rotated tokens before sending.
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
                { Content = JsonContent.Create(new { raw }) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _http.SendAsync(request, token);
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new MailAuthorizationException();
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // Rate limiting is retryable; a permission problem needs user action.
                    var body = await response.Content.ReadAsStringAsync(token);
                    if (!body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
                        !body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
                        !body.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
                        !body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase)) throw new MailAuthorizationException();
                }
                throw new MailProviderException(Localization.Format("Gmail API returned HTTP {0}; message acceptance not confirmed.",
                    "Gmail API вернул HTTP {0}; приём письма не подтверждён.", (int)response.StatusCode));
            }
        }
        catch (MailAuthorizationException)
        {
            if (_session is not null) { _session.NeedsAuthorization = true; _vault.Save(_session); }
            throw;
        }
        finally { _gate.Release(); }
    }
}
