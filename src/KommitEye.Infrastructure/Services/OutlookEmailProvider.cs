using KommitEye.Platform;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KommitEye.Services;

public sealed class OutlookEmailProvider : IEmailProvider
{
    private readonly OAuthVault _vault;
    private readonly IMicrosoftOAuthClient _oauth;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MicrosoftMailSession? _session;
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public string Id => "outlook";
    public EmailAccount? Account => _session is { } s ? new(Id, s.AccountId, s.Email, s.NeedsAuthorization) : null;
    public string? StatusError { get; private set; }

    public OutlookEmailProvider(string directory, IMicrosoftOAuthClient? oauth = null, HttpClient? http = null, ISecretProtector? protector = null)
    {
        _vault = new OAuthVault(Path.Combine(directory, "oauth", "outlook-account.bin"), protector);
        _oauth = oauth ?? new MicrosoftOAuthClient();
        _http = http ?? DefaultHttp;
        try
        {
            _session = _vault.Load<MicrosoftMailSession>();
            if (_session is { } s && (!Guid.TryParse(s.ClientId, out _) || string.IsNullOrWhiteSpace(s.AccountId) ||
                string.IsNullOrWhiteSpace(s.Email) || s.Cache.Length == 0)) throw new InvalidDataException();
        }
        catch
        {
            _session = null;
            StatusError = Localization.Text("Saved Microsoft authorization cannot be read. Reconnect your account.",
                "Не удалось прочитать сохранённую авторизацию Microsoft. Подключите аккаунт заново.");
        }
    }

    public async Task<EmailAccount> ConnectAsync(string clientFile, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var clientId = clientFile.Trim();
            string? tenantId = null;
            if (clientId.StartsWith("{"))
            {
                using var config = JsonDocument.Parse(clientId);
                clientId = config.RootElement.GetProperty("clientId").GetString() ?? "";
                tenantId = config.RootElement.GetProperty("tenantId").GetString();
            }
            if (string.IsNullOrWhiteSpace(clientId)) clientId = _session?.ClientId ?? "";
            if (string.IsNullOrWhiteSpace(tenantId)) tenantId = _session?.TenantId ?? "common";
            if (!Guid.TryParse(clientId, out _)) throw new MailProviderException(Localization.Text(
                "Enter Application (client) ID from Overview, not a client secret or Secret ID.",
                "Укажите Application (client) ID из Overview, а не секрет или Secret ID."));
            _ = MicrosoftOAuthClient.Authority(tenantId);
            var session = await _oauth.ConnectAsync(clientId, tenantId.Trim(), token);
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
        try { _vault.Delete(); _session = null; StatusError = null; }
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
                throw new MailProviderException(Localization.Text("Queued message belongs to another mailbox; sending is blocked.",
                    "Письмо в очереди принадлежит другому ящику; отправка заблокирована."));
            using var message = MailNotifications.CreateMessage(job);
            using var bytes = new MemoryStream();
            await message.WriteToAsync(bytes, token);
            var mime = Convert.ToBase64String(bytes.ToArray());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var access = await _oauth.AccessTokenAsync(session, attempt > 0, token);
                _vault.Save(session);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
                { Content = new StringContent(mime, Encoding.ASCII, "text/plain") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using var response = await _http.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.Accepted) return;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new MailAuthorizationException();
                throw new MailProviderException(Localization.Format("Microsoft Graph returned HTTP {0}; message acceptance not confirmed.",
                    "Microsoft Graph вернул HTTP {0}; приём письма не подтверждён.", (int)response.StatusCode));
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
