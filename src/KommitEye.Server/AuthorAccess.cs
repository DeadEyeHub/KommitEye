using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KommitEye.Platform;
using KommitEye.Services;
using MimeKit;

namespace KommitEye.Server;

public sealed record AuthorPortalSettings
{
    public bool Enabled { get; init; }
    public string PublicUrl { get; init; } = "";
    public int CodeMinutes { get; init; } = 10;
    public int ResendSeconds { get; init; } = 60;
    public int SendsPerHour { get; init; } = 5;
    public int MaxAttempts { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public int SessionHours { get; init; } = 8;

    public void Validate()
    {
        if (CodeMinutes is < 1 or > 60 || ResendSeconds is < 30 or > 3600 || SendsPerHour is < 1 or > 30 ||
            MaxAttempts is < 1 or > 10 || LockoutMinutes is < 1 or > 1440 || SessionHours is < 1 or > 720)
            throw new ArgumentException("Invalid sign-in limits. Check the allowed ranges.");
        if ((Enabled || PublicUrl.Length > 0) && (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/"))
            throw new ArgumentException("Enter the public HTTPS origin, for example https://reviews.example.com (localhost HTTP is allowed).");
    }
}

public sealed class AuthorAccess
{
    public sealed class Challenge
    {
        public string Hash { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
        public DateTimeOffset LastSent { get; set; }
        public List<DateTimeOffset> Sends { get; set; } = [];
        public int Attempts { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
    }
    public sealed class State
    {
        public AuthorPortalSettings Settings { get; set; } = new();
        public long Revision { get; set; }
        public string SessionVersion { get; set; } = Guid.NewGuid().ToString("N");
        public string ProtectedKey { get; set; } = "";
        public Dictionary<string, Challenge> Challenges { get; set; } = [];
    }
    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[] _key;
    private readonly Func<DateTimeOffset> _now;
    private readonly State _state;
    public AuthorAccess(string directory, ISecretProtector protector, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(directory, "author-access.json");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _state = File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Invalid author access store.") : new();
        _state.Settings.Validate();
        if (_state.ProtectedKey.Length == 0)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            _state.ProtectedKey = Convert.ToBase64String(protector.Protect(_key));
            Save();
        }
        else _key = protector.Unprotect(Convert.FromBase64String(_state.ProtectedKey));
    }
    public AuthorPortalSettings Settings { get { lock (_gate) return _state.Settings; } }
    public object Snapshot() { lock (_gate) return new { settings = _state.Settings, revision = _state.Revision }; }
    public string SessionVersion { get { lock (_gate) return _state.SessionVersion; } }
    public void Update(AuthorPortalSettings settings, long revision)
    {
        settings.Validate();
        lock (_gate)
        {
            if (revision != _state.Revision) throw new InvalidOperationException("Settings changed. Reload before saving.");
            _state.Settings = settings with { PublicUrl = settings.PublicUrl.TrimEnd('/') };
            _state.Revision++;
            // Changing access limits invalidates outstanding codes and sessions immediately.
            _state.SessionVersion = Guid.NewGuid().ToString("N");
            _state.Challenges.Clear();
            Save();
        }
    }
    public static string Normalize(string? email)
    {
        var value = email?.Trim() ?? "";
        if (value.Length is < 3 or > 254 || value.Any(char.IsControl) || !value.Contains('@') ||
            !MailboxAddress.TryParse(value, out var parsed) || !parsed.Address.Equals(value, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid email address.");
        return value.ToLowerInvariant();
    }
    private string Hash(string email, string code) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(email + "\n" + code)));
    public (string Code, DateTimeOffset Expires)? Issue(string email)
    {
        lock (_gate)
        {
            if (!_state.Settings.Enabled) throw new InvalidOperationException("Author portal is disabled.");
            var now = _now(); var settings = _state.Settings;
            foreach (var stale in _state.Challenges.Where(p => p.Value.LastSent < now.AddHours(-2) && p.Value.BlockedUntil <= now).Select(p => p.Key).ToArray())
                _state.Challenges.Remove(stale);
            if (!_state.Challenges.TryGetValue(email, out var challenge))
            {
                if (_state.Challenges.Count >= 10000) return null;
                _state.Challenges[email] = challenge = new();
            }
            challenge.Sends.RemoveAll(time => time <= now.AddHours(-1));
            if (challenge.BlockedUntil > now || challenge.LastSent.AddSeconds(settings.ResendSeconds) > now || challenge.Sends.Count >= settings.SendsPerHour) return null;
            var code = RandomNumberGenerator.GetInt32(1000000).ToString("D6");
            challenge.Hash = Hash(email, code); challenge.Expires = now.AddMinutes(settings.CodeMinutes);
            challenge.LastSent = now; challenge.Sends.Add(now);
            // Resending must not reset the attempt budget; only an elapsed lockout does.
            if (challenge.BlockedUntil != default && challenge.BlockedUntil <= now) { challenge.Attempts = 0; challenge.BlockedUntil = default; }
            Save();
            return (code, challenge.Expires);
        }
    }
    public bool Verify(string email, string code)
    {
        lock (_gate)
        {
            var now = _now();
            if (!_state.Settings.Enabled || !_state.Challenges.TryGetValue(email, out var challenge) ||
                challenge.BlockedUntil > now || challenge.Expires <= now || challenge.Hash.Length == 0) return false;
            var valid = code.Length == 6 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge.Hash), Convert.FromHexString(Hash(email, code)));
            challenge.Attempts++;
            if (valid) { challenge.Hash = ""; challenge.Attempts = 0; }
            else if (challenge.Attempts >= _state.Settings.MaxAttempts)
            { challenge.Hash = ""; challenge.BlockedUntil = now.AddMinutes(_state.Settings.LockoutMinutes); }
            Save();
            return valid;
        }
    }
    public (string Version, int Hours)? Authenticate(string email, string code)
    {
        lock (_gate) return Verify(email, code) ? (_state.SessionVersion, _state.Settings.SessionHours) : null;
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (var stream = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, _state); stream.Flush(true); }
        File.Move(_path + ".tmp", _path, true);
    }
}
