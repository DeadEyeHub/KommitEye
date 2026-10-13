using System.Security.Cryptography;
using System.Text.Json;

namespace KommitEye.Server;

public sealed class ServerSecurity
{
    private sealed record PasswordData(string Salt, string Hash);
    private readonly PasswordData _password;
    public ServerSecurity(string directory)
    {
        var path = Path.Combine(directory, "admin.json");
        if (File.Exists(path)) _password = JsonSerializer.Deserialize<PasswordData>(File.ReadAllText(path))!;
        else
        {
            var password = Environment.GetEnvironmentVariable("KOMMITEYE_ADMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(password))
            {
                password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
                File.WriteAllText(Path.Combine(directory, "initial-password.txt"), password);
                Console.WriteLine("Initial administrator password is in " + Path.Combine(directory, "initial-password.txt"));
            }
            if (password.Length < 12) throw new InvalidOperationException("Administrator password must contain at least 12 characters.");
            var salt = RandomNumberGenerator.GetBytes(32);
            _password = new(Convert.ToBase64String(salt), Convert.ToBase64String(Hash(password, salt)));
            File.WriteAllText(path, JsonSerializer.Serialize(_password));
        }
    }
    private static byte[] Hash(string value, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(value, salt, 210000, HashAlgorithmName.SHA256, 32);
    public bool Verify(string password) => password.Length <= 1024 && CryptographicOperations.FixedTimeEquals(
        Hash(password, Convert.FromBase64String(_password.Salt)), Convert.FromBase64String(_password.Hash));
}
