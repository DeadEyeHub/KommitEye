using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace KommitEye.Platform;

/// <summary>Retains the original CurrentUser DPAPI format, including null entropy.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
