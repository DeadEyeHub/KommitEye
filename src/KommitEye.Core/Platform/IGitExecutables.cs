namespace KommitEye.Platform;

/// <summary>Resolves platform tools without owning repository or authentication policy.</summary>
public interface IGitExecutables
{
    string GitFileName { get; }
    bool SupportsPutty { get; }
    string? FindAgentSsh();
    string? FindPlink();
}
