using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    using (var rsa = RSA.Create(2048))
    {
        var request = new CertificateRequest("CN=KommitEye platform fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var original = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(directory, "fixture.pfx");
        File.WriteAllBytes(certificatePath, original.Export(X509ContentType.Pfx, "fixture-password"));
        var data = Encoding.UTF8.GetBytes("platform key import fixture");
        byte[] signature;
        using (var loaded = Pkcs12Certificates.Load(certificatePath, "fixture-password"))
        using (var key = loaded.GetRSAPrivateKey())
        {
            Check(key is not null, "PFX loader imports a usable private key on this platform");
            signature = key!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        using (var reloaded = Pkcs12Certificates.Load(certificatePath, "fixture-password"))
        using (var key = reloaded.GetRSAPublicKey())
            Check(key!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "PFX can be reloaded after disposing the first import");
        try { using var invalid = Pkcs12Certificates.Load(certificatePath, "wrong-password"); throw new Exception("Incorrect PFX password accepted"); }
        catch (CryptographicException) { Console.WriteLine("PASS: PFX loader rejects incorrect passwords"); }
    }
    var posix = new PosixGitExecutables();
    Check(posix.GitFileName == "git" && posix.FindAgentSsh() == "ssh", "POSIX uses Git and OpenSSH from PATH");
    Check(!posix.SupportsPutty && posix.FindPlink() is null, "POSIX explicitly excludes Windows PuTTY mode");
    Check(GitExecutables.Current.GitFileName == (OperatingSystem.IsWindows() ? "git.exe" : "git"), "host selects platform Git tools");
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
    restored.Host = "smtp.example.invalid";
    restored.From = "sender@example.invalid";
    restored.Recipients = "recipient@example.invalid";
    restored.Enabled = true;
    await using (var mail = new MailNotifications(directory, _ => { }, startWorker: false, providers: [], protector: protector))
    {
        mail.SaveSettings(restored);
        Check(mail.Settings.GetPassword(protector) == "injected-password", "mail settings validation uses the injected protector");
    }
    await using (var mail = new MailNotifications(directory, _ => { }, startWorker: false, providers: [], protector: protector))
        Check(mail.Settings.GetPassword(protector) == "injected-password", "mail service reload retains encrypted credentials");
    restored.SetPassword("", protector);
    Check(restored.EncryptedPassword == "" && restored.GetPassword(protector) == "", "empty password stays empty");
    var customPath = Path.Combine(directory, "custom.bin");
    new OAuthVault(customPath, protector).Save(new Credential("injected-token"));
    Check(new OAuthVault(customPath, protector).Load<Credential>()?.Token == "injected-token", "OAuth accepts host-selected protector");
    var corrupt = File.ReadAllBytes(customPath); corrupt[^1] ^= 1; File.WriteAllBytes(customPath, corrupt);
    try { new OAuthVault(customPath, protector).Load<Credential>(); throw new Exception("Tampered vault accepted"); }
    catch (CryptographicException) { Console.WriteLine("PASS: corrupt ciphertext is rejected"); }
    if (!OperatingSystem.IsWindows())
    {
        var unconfiguredPath = Path.Combine(directory, "unconfigured.bin");
        try { new OAuthVault(unconfiguredPath).Save(new Credential("must-not-be-written")); throw new Exception("Missing protector accepted"); }
        catch (PlatformNotSupportedException) { Check(!File.Exists(unconfiguredPath), "unconfigured non-Windows host never saves plaintext credentials"); }
    }
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
