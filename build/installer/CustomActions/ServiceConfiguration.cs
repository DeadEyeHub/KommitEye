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

}
