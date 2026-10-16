using System.Text.Json;
using System.Text.Json.Serialization;

namespace KommitEye.Services;

// Service-only storage. Desktop retains its existing on-disk format.
internal sealed class MailSettingsStore
{
    private sealed class Document
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string ActiveProvider { get; set; } = "smtp";
        [JsonRequired] public Dictionary<string, MailSettings> Providers { get; set; } = new();
    }
    private static readonly string[] Providers = ["smtp", "gmail", "outlook"];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private Document _document;
    public MailSettings Active => Get(_document.ActiveProvider);

    public MailSettingsStore(string path, Action<string> log)
    {
        _path = path;
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        using var json = text is null ? null : JsonDocument.Parse(text);
        if (json is not null && json.RootElement.EnumerateObject().Any(p =>
                p.Name.Equals("activeProvider", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("providers", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            _document = JsonSerializer.Deserialize<Document>(text!, Options) ?? throw new InvalidDataException("Invalid mail settings.");
            if (_document.Version != 1 || !Providers.Contains(_document.ActiveProvider) || _document.Providers is null ||
                !_document.Providers.ContainsKey(_document.ActiveProvider) ||
                _document.Providers.Any(pair => !Providers.Contains(pair.Key) || pair.Value is null || pair.Value.Provider != pair.Key))
                throw new InvalidDataException("Invalid mail settings document.");
        }
        else
        {
            var active = text is null ? new MailSettings() : JsonSerializer.Deserialize<MailSettings>(text, Options)
                ?? throw new InvalidDataException("Invalid legacy mail settings.");
            ValidateProvider(active.Provider);
            _document = new Document { ActiveProvider = active.Provider };
            foreach (var provider in Providers)
            {
                var legacyPath = path + "." + provider;
                // The active settings are authoritative; its sidecar may be stale.
                if (provider == active.Provider || !File.Exists(legacyPath)) continue;
                var settings = JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(legacyPath), Options)
                    ?? throw new InvalidDataException("Invalid legacy mail settings.");
                if (settings.Provider != provider) throw new InvalidDataException("Legacy mail provider mismatch.");
                _document.Providers[provider] = settings;
            }
            _document.Providers[active.Provider] = active;
            Write(_document);
        }
        // Retry cleanup after interrupted migration; the unified file is now authoritative.
        foreach (var provider in Providers)
        {
            try { File.Delete(path + "." + provider); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { log("Mail: migrated settings; old provider file could not be removed (" + error.GetType().Name + ")."); }
        }
    }

    public MailSettings Get(string provider)
    {
        ValidateProvider(provider);
        return _document.Providers.TryGetValue(provider, out var settings) ? settings.Clone() : new MailSettings { Provider = provider };
    }

    public void Save(MailSettings settings, bool activate)
    {
        ValidateProvider(settings.Provider);
        var next = new Document { ActiveProvider = activate ? settings.Provider : _document.ActiveProvider,
            Providers = new(_document.Providers) { [settings.Provider] = settings.Clone() } };
        Write(next);
        _document = next;
    }

    private static void ValidateProvider(string provider)
    {
        if (!Providers.Contains(provider)) throw new ArgumentException("Unknown mail provider.");
    }

    private void Write(Document document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, document, Options); file.Flush(true); }
        File.Move(_path + ".tmp", _path, true);
    }
}
