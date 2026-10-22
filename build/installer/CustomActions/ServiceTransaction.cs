using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Web.Script.Serialization;
using WixToolset.Dtf.WindowsInstaller;

namespace KommitEye.Setup;

public static partial class Actions
{
    [CustomAction]
    public static ActionResult PrepareServiceTransaction(Session session)
    {
        try
        {
            // Program Files inherits an administrator-controlled ACL. Never execute elevated
            // tools from the interactive user's writable TEMP directory.
            var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KommitEye Setup", Guid.NewGuid().ToString("N"));
            if (session["REMOVE"] == "ALL")
            {
                using (var saved = Saved())
                    foreach (var property in new[] { "INSTALLFOLDER", "CONFIGDIR" })
                        if (saved?.GetValue(property) is string value) session[property] = value;
            }
            var values = new CustomActionData {
                { "stage", stage }, { "exe", Path.Combine(session["INSTALLFOLDER"], "KommitEye.Server.exe") },
                { "config", Path.Combine(session["CONFIGDIR"], "server.json") },
                { "account", session["SERVICEACCOUNT"] }, { "password", session["SERVICEPASSWORD"] },
                { "automatic", session["AUTOSTART"] == "1" ? "automatic" : "manual" },
                { "start", session["STARTNOW"] }, { "remove", session["REMOVE"] == "ALL" ? "1" : "0" }
            };
            foreach (var name in new[] { "BeginServiceTransaction", "RollbackServiceTransaction", "RollbackNewService", "RegisterServer", "CommitServiceTransaction" })
                session[name] = values.ToString();
            return ActionResult.Success;
        }
        catch (Exception error) { return Error(session, "Cannot prepare service transaction: " + error.Message); }
    }

    [CustomAction]
    public static ActionResult BeginServiceTransaction(Session session) => ServiceAction(session, values =>
    {
        var stage = values["stage"];
        Directory.CreateDirectory(stage);
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ServiceTools.zip"))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid service tool archive path.");
                if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target);
            }
        }
        RunServiceTool(session, values, "--service-snapshot " + Quote(Path.Combine(stage, "snapshot.json")));
        RunServiceTool(session, values, "--stop-service");
    });

    [CustomAction]
    public static ActionResult RegisterServer(Session session) => ServiceAction(session, values =>
    {
        RunServiceTool(session, values, "--install-service --update-service --service-executable " + Quote(values["exe"]) +
            " --config " + Quote(values["config"]) + " --service-account " + Quote(values["account"]) +
            " --service-start " + values["automatic"] + (values["password"] == "" ? "" : " --service-password-stdin"));
        if (values["start"] == "1") RunServiceTool(session, values, "--start-service");
    });

    [CustomAction]
    public static ActionResult RollbackNewService(Session session) => ServiceAction(session, values =>
    {
        var path = Path.Combine(values["stage"], "snapshot.json");
        if (!File.Exists(path)) return;
        var snapshot = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        RunServiceTool(session, values, (bool)snapshot["Exists"] ? "--stop-service" : "--uninstall-service");
    });

    [CustomAction]
    public static ActionResult RollbackServiceTransaction(Session session) => ServiceAction(session, values =>
    {
        var path = Path.Combine(values["stage"], "snapshot.json");
        if (File.Exists(path)) RunServiceTool(session, values, "--restore-service " + Quote(path) +
            (values["password"] == "" ? "" : " --service-password-stdin"));
        CleanupTools(values);
    });

    [CustomAction]
    public static ActionResult CommitServiceTransaction(Session session) => ServiceAction(session, values =>
    {
        // Keep an existing service's SCM credentials through uninstall rollback. Delete
        // only at commit, after files have been removed; the staged runtime still exists.
        if (values["remove"] == "1") RunServiceTool(session, values, "--uninstall-service");
        CleanupTools(values);
    });

    private static void CleanupTools(CustomActionData values)
    {
        var stage = Path.GetFullPath(values["stage"]);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KommitEye Setup") + Path.DirectorySeparatorChar;
        if (!stage.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new IOException("Refusing cleanup outside the setup staging directory.");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
    }
    private static ActionResult ServiceAction(Session session, Action<CustomActionData> action)
    {
        try { action(session.CustomActionData); return ActionResult.Success; }
        catch (Exception error) { session.Log("Service transaction failed: " + error.Message); return Error(session, error.Message); }
    }
    private static void RunServiceTool(Session session, CustomActionData values, string arguments)
    {
        using (var child = Process.Start(new ProcessStartInfo(Path.Combine(values["stage"], "KommitEye.Server.exe"), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true }))
        {
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (arguments.Contains("--service-password-stdin")) child.StandardInput.WriteLine(values["password"]);
            child.StandardInput.Close();
            if (!child.WaitForExit(90000)) { child.Kill(); throw new IOException("Service operation timed out."); }
            session.Log(output.GetAwaiter().GetResult());
            if (child.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult());
        }
    }
}
