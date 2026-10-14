using KommitEye.Platform;
using Microsoft.AspNetCore.DataProtection;

namespace KommitEye.Server.Hosting;

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("KommitEye.Credentials.v1");
    public byte[] Protect(byte[] plaintext) => _protector.Protect(plaintext);
    public byte[] Unprotect(byte[] ciphertext) => _protector.Unprotect(ciphertext);
}
