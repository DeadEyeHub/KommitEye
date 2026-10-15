using System.Security.Cryptography.X509Certificates;
using KommitEye.Platform;
using Microsoft.AspNetCore.DataProtection;

namespace KommitEye.Server.Hosting;

/// <summary>Server composition root; desktop hosts select their own platform services.</summary>
public sealed class ServerPlatform : IDisposable
{
    private readonly X509Certificate2? _certificate;
    private ServerPlatform(X509Certificate2? certificate) => _certificate = certificate;

    public static ServerPlatform Configure(WebApplicationBuilder builder, string dataDirectory, string credentialProtection = "CurrentUser")
    {
        var protection = builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "session-keys")));
        if (OperatingSystem.IsWindows())
            builder.Services.AddWindowsService(options => options.ServiceName = "KommitEye");
        var path = Environment.GetEnvironmentVariable("KOMMITEYE_KEY_CERTIFICATE");
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(path))
        {
            var machine = credentialProtection == "LocalMachine";
            protection.ProtectKeysWithDpapi(protectToLocalMachine: machine);
            builder.Services.AddSingleton<ISecretProtector>(machine ? new WindowsMachineSecretProtector() : new WindowsSecretProtector());
            return new(null);
        }

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Set KOMMITEYE_KEY_CERTIFICATE to a persistent PFX certificate with its private key on non-Windows servers.");
        var passwordFile = Environment.GetEnvironmentVariable("KOMMITEYE_KEY_CERTIFICATE_PASSWORD_FILE");
        var password = string.IsNullOrWhiteSpace(passwordFile) ? null : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        var certificate = Pkcs12Certificates.Load(path, password);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The server key certificate must include its private key.");
        }
        protection.SetApplicationName("KommitEye.Server").ProtectKeysWithCertificate(certificate);
        builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return new(certificate);
    }

    public void Dispose() => _certificate?.Dispose();
}
