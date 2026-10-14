using System.Reflection;
using System.Text;

namespace KommitEye.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();
    public static string ModelsConfig => Path.Combine(DataDirectory, "models.conf");
    public static string SettingsConfig => Path.Combine(DataDirectory, "settings.conf");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string ReportsDirectory => Path.Combine(DataDirectory, "reports");
    public static string JournalLog => Path.Combine(DataDirectory, "journal.log");
    public static string ModelLog => Path.Combine(DataDirectory, "model-log.log");

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("KOMMITEYE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("GITREVIEWER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "KommitEye");
        var legacy = Path.Combine(local, "GitReviewer");
        // Reuse existing settings, review state and encrypted credentials after upgrading.
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        if (!File.Exists(ModelsConfig))
            File.WriteAllText(ModelsConfig, ReadTemplate("models.example.conf"), Encoding.UTF8);
    }

    public static string ReadTemplate(string fileName)
    {
        var resourceName = $"KommitEye.Templates.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded template not found: {fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
