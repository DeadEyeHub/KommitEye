using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace KommitEye.Platform;

/// <summary>For service-owned stores protected by restrictive filesystem ACLs.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.LocalMachine);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.LocalMachine);
}
