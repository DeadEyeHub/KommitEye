using System;
using System.IO;
using System.Linq;

namespace KommitEye.Setup;

// Pure validation is shared with the installer checks; no MSI session or machine changes.
public static class SetupValidation
{
    public const string VirtualServiceAccount = @"NT SERVICE\KommitEye";
    public static string InstallationAccount(string installedAccount)
    {
        if (!string.IsNullOrEmpty(installedAccount) && !string.Equals(installedAccount, VirtualServiceAccount, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MSI supports only NT SERVICE\\KommitEye. Remove the differently configured service explicitly before installation; its data must be retained.");
        return VirtualServiceAccount;
    }

    public static string ListenUrls(string mode, string ip, int port, string[] localAddresses)
    {
        Port(port.ToString());
        var loopback = "http://127.0.0.1:" + port;
        if (mode == "local") return loopback;
        if (mode == "lan") return "http://0.0.0.0:" + port;
        if (mode != "ip" || !localAddresses.Contains(ip) ||
            !System.Net.IPAddress.TryParse(ip, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("Select an IPv4 address assigned to an active network adapter on this computer.");
        // Loopback remains available for the first administrator setup.
        return loopback + ";http://" + ip + ":" + port;
    }

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
            throw new ArgumentException("Use an absolute local folder path, for example C:\\ProgramData\\KommitEye\\Server\\data.");
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

}
