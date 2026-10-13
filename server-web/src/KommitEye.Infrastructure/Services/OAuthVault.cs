using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace KommitEye.Services;

// Tokens, account identity and client configuration are committed together.
// No plaintext file or plaintext temporary copy is created.
public sealed class OAuthVault(string path)
{
    public T? Load<T>() where T : class
    {
        if (!File.Exists(path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plain)) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save<T>(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(encrypted); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete() { File.Delete(path); File.Delete(path + ".tmp"); }
}
