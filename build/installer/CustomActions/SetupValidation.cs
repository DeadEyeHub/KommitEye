using System;
using System.IO;
using System.Linq;

namespace KommitEye.Setup;

// Pure validation is shared with the installer checks; no MSI session or machine changes.
public static class SetupValidation
{
    public const string VirtualServiceAccount = @"NT SERVICE\KommitEye";
    // Deterministic service SID for the fixed SCM name KommitEye (sc.exe showsid KommitEye).
    public const string VirtualServiceSid = "S-1-5-80-3680015686-339733092-3489414343-1427611537-2043285906";
    public static string InstallationAccount(string installedAccount) => string.IsNullOrEmpty(installedAccount)
        ? VirtualServiceAccount : string.Join("\\", Account(installedAccount));
    public static bool IsLegacyService(string installedAccount) => !string.IsNullOrEmpty(installedAccount) &&
        !InstallationAccount(installedAccount).Equals(VirtualServiceAccount, StringComparison.OrdinalIgnoreCase);

    public static int Port(string value)
    {
        if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
            throw new ArgumentException("Enter a TCP port from 1 to 65535.");
        return port;
    }

    public static string DirectoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' ||
            value.IndexOfAny(new[] { '"', '\r', '\n', ';' }) >= 0 || value.Substring(2).Contains(':'))
            throw new ArgumentException("Use an absolute local folder path, for example C:\\ProgramData\\KommitEye.Server.");
        var full = Path.GetFullPath(value).TrimEnd('\\');
        if (full.Length <= 2) throw new ArgumentException("Choose a subfolder, not the root of a drive.");
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("Choose a folder on a fixed local drive.");
        return full;
    }

    public static bool Overlaps(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static string[] Account(string value)
    {
        if (string.Equals(value, VirtualServiceAccount, StringComparison.OrdinalIgnoreCase)) return new[] { "NT SERVICE", "KommitEye" };
        var parts = (value ?? "").Trim().Split('\\');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(p => p.IndexOfAny(new[] { '"', ';', '\r', '\n' }) >= 0))
            throw new ArgumentException("Enter an existing Windows account as COMPUTER\\user or DOMAIN\\user.");
        if (parts[0] == ".") parts[0] = Environment.MachineName;
        if (parts[0].Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a regular Windows service account with a password.");
        return parts;
    }
}
