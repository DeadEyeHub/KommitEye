using System.Security.Cryptography.X509Certificates;

namespace KommitEye.Platform;

public static class Pkcs12Certificates
{
    /// <summary>Loads a PFX using the platform's supported temporary key storage.
    /// The caller must dispose the returned certificate.</summary>
    public static X509Certificate2 Load(string path, string? password = null)
    {
        // macOS needs a temporary on-disk keychain and rejects EphemeralKeySet.
        // DefaultKeySet lets .NET manage that keychain's lifetime; do not persist it.
        var flags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
    }
}
