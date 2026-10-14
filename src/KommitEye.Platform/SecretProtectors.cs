namespace KommitEye.Platform;

public static class SecretProtectors
{
    // Compatibility default for existing desktop callers. Other hosts inject their
    // own protector; never silently fall back to plaintext or ephemeral keys.
    public static ISecretProtector CurrentUser { get; } = OperatingSystem.IsWindows()
        ? new WindowsSecretProtector() : new UnconfiguredSecretProtector();

    private sealed class UnconfiguredSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => throw MissingConfiguration();
        public byte[] Unprotect(byte[] ciphertext) => throw MissingConfiguration();
        private static PlatformNotSupportedException MissingConfiguration() =>
            new("This host must configure a persistent ISecretProtector before using saved credentials.");
    }
}
