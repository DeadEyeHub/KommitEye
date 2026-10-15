using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace KommitEye.Setup;

[DataContract]
public sealed class ServiceConfiguration
{
    [DataMember] public string DataDirectory { get; set; } = "";
    [DataMember] public string Urls { get; set; } = "";
    [DataMember] public string GitExecutable { get; set; } = "";
    [DataMember] public string CredentialProtection { get; set; } = "";

    public static ServiceConfiguration Read(string path)
    {
        using (var stream = File.OpenRead(path))
            return new DataContractJsonSerializer(typeof(ServiceConfiguration)).ReadObject(stream) as ServiceConfiguration
                ?? throw new SerializationException("Configuration must be a JSON object.");
    }

    // Configuration is persistent administrator-owned data, never overwritten by repair/upgrade.
    public bool CreateIfMissing(string path)
    {
        if (File.Exists(path)) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(ServiceConfiguration)).WriteObject(stream, this); stream.Flush(true); }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
