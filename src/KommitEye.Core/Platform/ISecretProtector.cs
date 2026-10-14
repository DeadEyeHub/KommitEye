namespace KommitEye.Platform;

/// <summary>Protects persisted credentials using a host-selected key store.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
