using System.Security.Cryptography;
using System.Text.Json;

namespace KommitEye.Server;

public sealed class ServerSecurity
{
    private sealed record PasswordData(string Salt, string Hash);
    private readonly object _gate = new();
    private readonly string _path;
    private PasswordData? _password;
    public bool NeedsSetup { get { lock (_gate) return _password is null; } }
    public ServerSecurity(string directory)
    {
        _path = Path.Combine(directory, "admin.json");
        if (File.Exists(_path)) _password = JsonSerializer.Deserialize<PasswordData>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Invalid administrator configuration.");
        else
        {
            var password = Environment.GetEnvironmentVariable("KOMMITEYE_ADMIN_PASSWORD");
            if (!string.IsNullOrEmpty(password)) Setup(password);
            else Console.WriteLine("Open this server in a browser on localhost to create your administrator password.");
        }
    }
    public void Setup(string password)
    {
        lock (_gate)
        {
            if (_password is not null) throw new InvalidOperationException("Administrator password is already configured. Sign in.");
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter a password.");
            var salt = RandomNumberGenerator.GetBytes(32);
            var value = new PasswordData(Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt)));
            var temporary = _path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value); stream.Flush(true);
            }
            File.Move(temporary, _path, false);
            _password = value;
        }
    }
    private static byte[] Hash(string value, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(value, salt, 210000, HashAlgorithmName.SHA256, 32);
    public bool Verify(string password)
    {
        PasswordData? saved;
        lock (_gate) saved = _password;
        return saved is not null && CryptographicOperations.FixedTimeEquals(
            Hash(password, Convert.FromBase64String(saved.Salt)), Convert.FromBase64String(saved.Hash));
    }
}
