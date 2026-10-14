using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KommitEye.Platform;
using KommitEye.Services;

var directory = Path.Combine(Path.GetTempPath(), "kommiteye-platform-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
try
{
    if (OperatingSystem.IsWindows())
    {
        var legacy = ProtectedData.Protect(Encoding.UTF8.GetBytes("old-password"), null, DataProtectionScope.CurrentUser);
        var settings = new MailSettings { EncryptedPassword = Convert.ToBase64String(legacy) };
        Check(settings.GetPassword() == "old-password", "existing SMTP DPAPI ciphertext remains readable");
        settings.SetPassword("new-password");
        Check(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.EncryptedPassword), null,
            DataProtectionScope.CurrentUser)) == "new-password", "new SMTP ciphertext retains legacy format");
        var legacyPath = Path.Combine(directory, "legacy.bin");
        File.WriteAllBytes(legacyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Token\":\"old-token\"}"), null, DataProtectionScope.CurrentUser));
        var vault = new OAuthVault(legacyPath);
        Check(vault.Load<Credential>()?.Token == "old-token", "existing OAuth DPAPI ciphertext remains readable");
        vault.Save(new Credential("new-token"));
        var json = ProtectedData.Unprotect(File.ReadAllBytes(legacyPath), null, DataProtectionScope.CurrentUser);
        Check(JsonSerializer.Deserialize<Credential>(json)?.Token == "new-token", "new OAuth ciphertext retains legacy format");
    }
    using var protector = new FixtureProtector();
    var customSettings = new MailSettings();
    customSettings.SetPassword("injected-password", protector);
    var restored = JsonSerializer.Deserialize<MailSettings>(JsonSerializer.Serialize(customSettings))!;
    Check(restored.GetPassword(protector) == "injected-password", "SMTP accepts host-selected protector after serialization");
    restored.SetPassword("", protector);
    Check(restored.EncryptedPassword == "" && restored.GetPassword(protector) == "", "empty password stays empty");
    var customPath = Path.Combine(directory, "custom.bin");
    new OAuthVault(customPath, protector).Save(new Credential("injected-token"));
    Check(new OAuthVault(customPath, protector).Load<Credential>()?.Token == "injected-token", "OAuth accepts host-selected protector");
    var corrupt = File.ReadAllBytes(customPath); corrupt[^1] ^= 1; File.WriteAllBytes(customPath, corrupt);
    try { new OAuthVault(customPath, protector).Load<Credential>(); throw new Exception("Tampered vault accepted"); }
    catch (CryptographicException) { Console.WriteLine("PASS: corrupt ciphertext is rejected"); }
}
finally { Directory.Delete(directory, true); }

public sealed record Credential(string Token);

// Test-only key; production hosts must provide durable, separately protected keys.
sealed class FixtureProtector : ISecretProtector, IDisposable
{
    private readonly AesGcm _aes = new(RandomNumberGenerator.GetBytes(32), 16);
    public byte[] Protect(byte[] plaintext)
    {
        var output = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        _aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16));
        return output;
    }
    public byte[] Unprotect(byte[] ciphertext)
    {
        var output = new byte[ciphertext.Length - 28];
        _aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), output);
        return output;
    }
    public void Dispose() => _aes.Dispose();
}
