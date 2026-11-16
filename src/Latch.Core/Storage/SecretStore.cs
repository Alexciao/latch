using System.Text.Json;
using Latch.Core.Contract;
using Latch.Core.Model;

namespace Latch.Core.Storage;

/// <summary>secrets.json: PIN hash, protected TOTP secret and the lockout state. Atomic, with `.bak`.</summary>
public sealed class SecretStore(string path)
{
    private readonly Lock _gate = new();

    public SecretsFile Load() =>
        AtomicFile.Read(path, bytes => JsonSerializer.Deserialize(bytes, ContractJson.Default.SecretsFile)) ?? new SecretsFile();

    public void Save(SecretsFile secrets)
    {
        lock (_gate) AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(secrets, ContractJson.Default.SecretsFile));
    }
}
