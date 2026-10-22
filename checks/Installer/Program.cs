using KommitEye.Setup;

int count = 0;
void Check(bool result, string message) { if (!result) throw new Exception(message); count++; Console.WriteLine("PASS: " + message); }
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { Check(true, message); return; }
    throw new Exception(message);
}
Check(SetupValidation.InstallationAccount(null!) == SetupValidation.VirtualServiceAccount && !SetupValidation.IsLegacyService(null!), "new installation uses managed identity and skips account dialog");
Check(!SetupValidation.IsLegacyService(@"nt service\kommiteye"), "managed service upgrade skips account dialog");
Check(SetupValidation.IsLegacyService(@"DOMAIN\existing") && SetupValidation.InstallationAccount(@"DOMAIN\existing") == @"DOMAIN\existing", "legacy service upgrade preserves identity and requests password");
Check(SetupValidation.Port("5080") == 5080, "default port");
Check(SetupValidation.Port("65535") == 65535, "highest TCP port");
foreach (var value in new[] { "0", "65536", "-1", "abc", "80 --urls", "" }) Reject(() => SetupValidation.Port(value), "reject port " + value);
foreach (var value in new[] { @"relative\data", @"\\server\share", @"C:\", @"C:relative", "C:\\data\" --urls foo", @"C:\data:stream", @"C:\data;injection" })
    Reject(() => SetupValidation.DirectoryPath(value), "reject unsafe data path");
var basePath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "KommitEyeFixture");
Check(SetupValidation.DirectoryPath(basePath + @"\a\..\data\") == basePath + @"\data", "normalize local data path");
Check(SetupValidation.Overlaps(basePath, basePath + @"\data"), "reject data inside program folder");
Check(SetupValidation.Overlaps(basePath + @"\data", basePath), "reject program inside data folder");
Check(!SetupValidation.Overlaps(basePath, basePath + "Other"), "similar folder prefixes remain separate");
Check(SetupValidation.Account(@".\svc")[0] == Environment.MachineName, "expand local account domain");
foreach (var value in new[] { "user", @"DOMAIN\", @"\user", @"NT AUTHORITY\SYSTEM", "DOMAIN\\user;injection" })
    Reject(() => SetupValidation.Account(value), "reject invalid or built-in service account");
Console.WriteLine($"{count} installer validation checks passed.");

Check(SetupValidation.Account(@"NT SERVICE\KommitEye")[1] == "KommitEye", "virtual service account needs no existing user");
var fixture = Path.Combine(Path.GetTempPath(), "kommiteye-config-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var path = Path.Combine(fixture, "server.json");
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { DataDirectory = basePath, Urls = "http://127.0.0.1:5080", GitExecutable = @"C:\Program Files\Git\cmd\git.exe", CredentialProtection = "LocalMachine" }));
    var original = File.ReadAllBytes(path);
    Check(ServiceConfiguration.Read(path).DataDirectory == basePath, "installer reads executable-owned configuration");
    Check(original.SequenceEqual(File.ReadAllBytes(path)), "installer reader preserves configuration byte for byte");
}
finally { Directory.Delete(fixture, true); }
