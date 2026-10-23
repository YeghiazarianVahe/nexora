using System.Security.Cryptography;
using Nexora.Contracts;

namespace Nexora.Api.Services;

/// <summary>
/// The server's job-signing identity: an ECDSA P-256 key created on first start and kept DPAPI-protected (current user) in the data folder.
/// Its public half is stamped into every helper the server publishes or serves; helpers run a job only if it is signed by this key.
/// Deleting the key file rotates it: every helper stamped with the old key then refuses jobs until it is replaced (that is also how all
/// helpers are revoked at once).
/// </summary>
public sealed class ServerTrust : IDisposable
{
    private readonly ECDsa _key;

    public ServerTrust(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, "server-trust.key");
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // a fresh random key unless the stored one imports below
        if (File.Exists(file))
        {
            try
            {
                var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
                _key.ImportPkcs8PrivateKey(pkcs8, out _);
                PublicKey = _key.ExportSubjectPublicKeyInfo();
                return;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // Unreadable (different Windows user, damaged): a new identity is created; helpers must be replaced.
                File.Move(file, file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            }
        }
        File.WriteAllBytes(file, ProtectedData.Protect(_key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
        PublicKey = _key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }
    public string Fingerprint => JobTrust.Fingerprint(PublicKey);
    public JobEnvelope Sign(JobPayload payload) => JobTrust.Sign(payload, _key);
    public void Dispose() => _key.Dispose();
}
