using KommitEye.Platform;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using KommitEye.Models;
using KommitEye.Services;

namespace KommitEye.Server;

public sealed class SettingsApi(ConfigurationStore configuration, ModelClient model, MailNotifications mail, ProjectCoordinator coordinator, ISecretProtector protector)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static string Revision(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/models", (SettingsApi settings) => settings.Models());
        api.MapPost("/models", (ModelUpdate update, SettingsApi settings) => settings.SaveModels(update));
        api.MapPost("/models/test", async (ModelProfile profile, SettingsApi settings, CancellationToken token) =>
        {
            if (profile.ApiKey.Length == 0) profile.ApiKey = configurationKey(settings.configuration, profile.Name);
            await settings.model.TestConnectionAsync(profile, token); return Results.Ok();
        });
        api.MapPost("/models/discover", async (ModelProfile profile, SettingsApi settings, CancellationToken token) =>
        {
            if (profile.ApiKey.Length == 0) profile.ApiKey = configurationKey(settings.configuration, profile.Name);
            return await settings.model.DiscoverModelsAsync(profile, token);
        });
        api.MapGet("/prompt", (string? language, SettingsApi settings) => { using var scope = Localization.UseLanguage(language ?? "en"); var text = settings.configuration.LoadPrompt(); return new { text, language = Localization.Language, revision = Revision(text) }; });
        api.MapPost("/prompt", async (PromptUpdate update, SettingsApi settings) =>
        {
            await settings._gate.WaitAsync();
            try
            {
                using var language = Localization.UseLanguage(update.Language);
                if (update.Revision != Revision(settings.configuration.LoadPrompt())) throw new InvalidOperationException("Prompt changed. Reload before saving.");
                settings.configuration.SavePrompt(update.Text); return Results.Ok();
            }
            finally { settings._gate.Release(); }
        });
        api.MapGet("/mail/{provider}", (string provider, SettingsApi settings) => settings.Mail(provider));
        api.MapPost("/mail/{provider}", (string provider, MailUpdate update, SettingsApi settings) => settings.SaveMail(provider, update, false));
        api.MapPost("/mail/{provider}/test", (string provider, MailUpdate update, SettingsApi settings) => settings.SaveMail(provider, update, true));
        api.MapPost("/mail/retry", (SettingsApi settings) => { settings.mail.RetryFailed(); return Results.Ok(); });
        api.MapPost("/mail/{provider}/disconnect", async (string provider, SettingsApi settings) => { await settings.mail.DisconnectAsync(provider, default); return Results.Ok(); });
    }
    private static string configurationKey(ConfigurationStore store, string name) => store.LoadModels().Profiles.FirstOrDefault(p => p.Name == name)?.ApiKey ?? "";
    private readonly ConfigurationStore configuration = configuration;
    private readonly ModelClient model = model;
    private readonly MailNotifications mail = mail;
    public object Models()
    {
        var models = configuration.LoadModels();
        return new { revision = Revision(models), activeProfile = models.ActiveProfile,
            profiles = models.Profiles.Select(p => new { profile = Redact(p), apiKeyConfigured = p.ApiKey.Length > 0 }) };
    }
    private static ModelProfile Redact(ModelProfile profile) { var copy = profile.Clone(); copy.ApiKey = ""; return copy; }
    public async Task<IResult> SaveModels(ModelUpdate update)
    {
        await _gate.WaitAsync();
        try
        {
            var old = configuration.LoadModels();
            if (update.Revision != Revision(old)) throw new InvalidOperationException("Models changed. Reload before saving.");
            if (update.Profiles.Count == 0 || update.Profiles.Select(p => p.Name).Distinct().Count() != update.Profiles.Count || !update.Profiles.Any(p => p.Name == update.ActiveProfile))
                throw new ArgumentException("Choose an active profile with a unique name.");
            var result = new ModelsConfiguration { ActiveProfile = update.ActiveProfile };
            foreach (var profile in update.Profiles)
            {
                foreach (var text in new[] { profile.Name, profile.Endpoint, profile.Model, profile.ApiKey, profile.ApiKeyEnvironment })
                    if (text.Any(char.IsControl) || text.Contains('[') || text.Contains(']')) throw new ArgumentException("Invalid profile text.");
                if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("Profile name is required.");
                profile.Parameters.Validate();
                if (profile.ApiKey.Length == 0 && !update.ClearKeys.Contains(profile.Name)) profile.ApiKey = old.Profiles.FirstOrDefault(p => p.Name == profile.Name)?.ApiKey ?? "";
                result.Profiles.Add(profile);
            }
            configuration.SaveModels(result);
            await coordinator.UpdateModelAsync(result.Profiles.Single(p => p.Name == result.ActiveProfile));
            return Results.Ok();
        }
        finally { _gate.Release(); }
    }
    public object Mail(string provider)
    {
        var value = mail.GetSettings(provider); var revision = Revision(value); var configured = value.EncryptedPassword.Length > 0;
        value.EncryptedPassword = "";
        return new { settings = value, revision, passwordConfigured = configured, account = mail.GetAccount(provider), active = mail.Settings.Provider == provider,
            pending = mail.Counts().Pending, failed = mail.Counts().Failed };
    }
    public async Task<IResult> SaveMail(string provider, MailUpdate update, bool test)
    {
        await _gate.WaitAsync();
        try
        {
            var old = mail.GetSettings(provider);
            if (update.Revision != Revision(old)) throw new InvalidOperationException("Mail settings changed. Reload before saving.");
            var settings = update.Settings; settings.Provider = provider; settings.AccountId = old.AccountId;
            settings.AuthorizationRequired = old.AuthorizationRequired; settings.EncryptedPassword = old.EncryptedPassword;
            if (update.ClearPassword) settings.SetPassword("", protector); else if (!string.IsNullOrEmpty(update.Password)) settings.SetPassword(update.Password, protector);
            if (provider != "smtp") settings.From = mail.GetAccount(provider)?.Address ?? "";
            if (test) await mail.TestAsync(settings, default, saveSettings: false);
            else mail.SaveSettings(settings, update.Activate);
            return Results.Ok();
        }
        finally { _gate.Release(); }
    }
}
public sealed record ModelUpdate(string Revision, string ActiveProfile, List<ModelProfile> Profiles, List<string> ClearKeys);
public sealed record PromptUpdate(string Revision, string Text, string Language = "en");
public sealed record MailUpdate(string Revision, MailSettings Settings, string? Password, bool ClearPassword, bool Activate);
