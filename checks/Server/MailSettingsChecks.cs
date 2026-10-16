using System.Text.Json;
using KommitEye.Services;

static class MailSettingsChecks
{
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "mail-migration");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mail-settings.json");
        var smtp = new MailSettings { Host = "current", EncryptedPassword = "opaque-encrypted-secret" };
        File.WriteAllText(path, JsonSerializer.Serialize(smtp));
        File.WriteAllText(path + ".smtp", JsonSerializer.Serialize(new MailSettings { Host = "stale" }));
        File.WriteAllText(path + ".gmail", JsonSerializer.Serialize(new MailSettings { Provider = "gmail", AccountId = "google-account", From = "google@example.invalid" }));
        await using (var mail = new MailNotifications(directory, _ => { }, startWorker: false, providers: [], unifiedSettings: true))
        {
            Check(mail.LoadError is null && mail.Settings.Host == "current", "migration keeps authoritative active settings");
            Check(mail.Settings.EncryptedPassword == smtp.EncryptedPassword, "migration preserves encrypted secret bytes");
            Check(mail.GetSettings("gmail").AccountId == "google-account", "migration keeps inactive provider settings");
            Check(!File.Exists(path + ".smtp") && !File.Exists(path + ".gmail"), "migration removes old files after save");
            var settings = mail.Settings;
            settings.Host = "updated";
            mail.SaveSettings(settings);
        }
        await using (var mail = new MailNotifications(directory, _ => { }, startWorker: false, providers: [], unifiedSettings: true))
            Check(mail.Settings.Host == "updated" && mail.GetSettings("gmail").AccountId == "google-account", "unified settings survive restart");

        // Updating an inactive SMTP profile must not change the selected provider.
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, activeProvider = "gmail", providers = json["providers"] }));
        await using (var mail = new MailNotifications(directory, _ => { }, startWorker: false, providers: [], unifiedSettings: true))
        {
            var settings = mail.GetSettings("smtp");
            settings.Host = "inactive-update";
            mail.SaveSettings(settings, activate: false);
            Check(mail.Settings.Provider == "gmail" && mail.GetSettings("smtp").Host == "inactive-update", "inactive save preserves active provider");
            mail.SaveSettings(settings);
            Check(mail.Settings.Provider == "smtp" && mail.GetSettings("gmail").AccountId == "google-account", "switch retains both profiles");
            var previous = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".tmp");
            settings.Host = "must-not-persist";
            try { mail.SaveSettings(settings); throw new Exception("Expected write failure"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Check(mail.Settings.Host == "inactive-update" && File.ReadAllText(path) == previous, "failed save leaves disk and active settings unchanged");
        }

        var broken = Path.Combine(root, "mail-broken");
        Directory.CreateDirectory(broken);
        var brokenPath = Path.Combine(broken, "mail-settings.json");
        var original = JsonSerializer.Serialize(smtp);
        File.WriteAllText(brokenPath, original);
        File.WriteAllText(brokenPath + ".gmail", "invalid JSON");
        await using (var mail = new MailNotifications(broken, _ => { }, startWorker: false, providers: [], unifiedSettings: true))
            Check(mail.LoadError is not null && File.ReadAllText(brokenPath) == original && File.Exists(brokenPath + ".gmail"), "failed migration preserves original files");
        File.WriteAllText(brokenPath + ".gmail", JsonSerializer.Serialize(new MailSettings { Provider = "gmail" }));
        Directory.CreateDirectory(brokenPath + ".tmp");
        await using (var mail = new MailNotifications(broken, _ => { }, startWorker: false, providers: [], unifiedSettings: true))
            Check(mail.LoadError is not null && File.ReadAllText(brokenPath) == original && File.Exists(brokenPath + ".gmail"), "migration write failure preserves all legacy files");

        var desktop = Path.Combine(root, "mail-desktop");
        await using (var mail = new MailNotifications(desktop, _ => { }, startWorker: false, providers: []))
            mail.SaveSettings(new MailSettings { Host = "desktop" });
        Check(File.Exists(Path.Combine(desktop, "mail-settings.json.smtp")), "desktop storage remains unchanged");
        Console.WriteLine("PASS: unified service mail settings migration, updates and recovery");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
