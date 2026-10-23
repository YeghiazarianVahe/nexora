using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexora.Contracts;

/// <summary>
/// One target of a job, as sent to the initiating operator's helper. Everything the helper needs is in here; it
/// never calls back for parameters.
/// </summary>
public sealed record JobPayload(
    Guid JobId, Guid TargetId, string ActionCode, string Host, string InitiatorSid,
    Dictionary<string, string> Parameters, int TimeoutSeconds, DateTime IssuedUtc, DateTime ExpiresUtc, string Nonce);

/// <summary>The serialized payload plus an ECDSA P-256 signature (base64, IEEE P1363) made with the Nexora server's private key.</summary>
public sealed record JobEnvelope(string Payload, string Signature);

/// <summary>
/// Server trust for jobs. The helper has the server's public key pinned inside its own executable (see <see cref="TrustStamp"/>); it
/// runs a job only if the signature verifies against that key. Whoever can answer on the network (plain HTTP, no certificates) but does
/// not hold the server's private key cannot make a helper run anything.
/// </summary>
public static class JobTrust
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JobEnvelope Sign(JobPayload payload, ECDsa serverKey)
    {
        var text = JsonSerializer.Serialize(payload, Json);
        return new JobEnvelope(text, Convert.ToBase64String(serverKey.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256)));
    }

    public static string Fingerprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki))[..16];

    public enum Error { None, NoPinnedKey, BadSignature, Expired, WrongOperator, UnknownAction, Malformed }

    /// <summary>
    /// The helper accepts a job only when a server key is pinned, the signature verifies against it, the job has not expired, it names this
    /// operator, and the action code is one the helper itself knows. Anything else is refused.
    /// </summary>
    public static (JobPayload? Payload, Error Error) Verify(
        JobEnvelope envelope, byte[]? pinnedPublicKey, string ownSid, DateTime utcNow, Func<string, bool> knownAction)
    {
        if (pinnedPublicKey is null || pinnedPublicKey.Length == 0) return (null, Error.NoPinnedKey);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(pinnedPublicKey, out _);
            var ok = key.VerifyData(Encoding.UTF8.GetBytes(envelope.Payload), Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256);
            if (!ok) return (null, Error.BadSignature);

            var payload = JsonSerializer.Deserialize<JobPayload>(envelope.Payload, Json);
            if (payload is null) return (null, Error.Malformed);
            if (utcNow > payload.ExpiresUtc || utcNow < payload.IssuedUtc.AddMinutes(-2)) return (null, Error.Expired);
            if (!string.Equals(payload.InitiatorSid, ownSid, StringComparison.OrdinalIgnoreCase)) return (null, Error.WrongOperator);
            if (!knownAction(payload.ActionCode)) return (null, Error.UnknownAction);
            return (payload, Error.None);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or CryptographicException)
        {
            return (null, Error.Malformed);
        }
    }
}

/// <summary>
/// Carries the server's public key inside the helper executable: <c>[original exe][public key (SPKI)][length u16][magic "NXTRUST1"]</c>.
/// The server stamps the file it publishes and every copy it serves for download, so the key travels with the helper wherever it is copied
/// (installHelper.ps1, the admin share, the Jobs page). Appended data after the PE image / single-file bundle does not affect execution.
/// </summary>
public static class TrustStamp
{
    private static readonly byte[] Magic = "NXTRUST1"u8.ToArray();

    public static byte[] Tail(byte[] spki)
    {
        var t = new byte[spki.Length + 2 + Magic.Length];
        spki.CopyTo(t, 0);
        BitConverter.GetBytes((ushort)spki.Length).CopyTo(t, spki.Length);
        Magic.CopyTo(t, spki.Length + 2);
        return t;
    }

    /// <summary>The key stamped at the end of the file, and the length of the file without the stamp.</summary>
    public static (byte[]? Key, long OriginalLength) Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(fs);
    }

    public static (byte[]? Key, long OriginalLength) Read(Stream s)
    {
        var tail = ReadTail(s, s.Length);
        if (tail.Key is not null) return tail;
        var certificate = CertificateOffset(s);
        if (certificate is { } offset)
            for (var padding = 0; padding < 8; padding++)
            {
                if (padding > 0) { s.Position = offset - padding; if (s.ReadByte() != 0) break; }
                var beforeCertificate = ReadTail(s, offset - padding);
                if (beforeCertificate.Key is not null) return beforeCertificate;
            }
        return (null, s.Length);
    }

    private static (byte[]? Key, long OriginalLength) ReadTail(Stream s, long len)
    {
        if (len < Magic.Length + 2 + 1) return (null, len);
        var tail = new byte[Magic.Length + 2];
        s.Seek(len - tail.Length, SeekOrigin.Begin);
        s.ReadExactly(tail);
        if (!tail.AsSpan(2).SequenceEqual(Magic)) return (null, len);
        int keyLen = BitConverter.ToUInt16(tail, 0);
        if (keyLen == 0 || keyLen + tail.Length > len) return (null, len);
        var key = new byte[keyLen];
        s.Seek(len - tail.Length - keyLen, SeekOrigin.Begin);
        s.ReadExactly(key);
        return (key, len - tail.Length - keyLen);
    }

    /// <summary>Detects a PE certificate table, not whether its signature is cryptographically valid.</summary>
    public static bool HasCertificateTable(string path)
    {
        using var stream = File.OpenRead(path);
        return CertificateOffset(stream) is not null;
    }
    private static long? CertificateOffset(Stream stream)
    {
        if (stream.Length < 64) return null;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen:true);
        stream.Position=0; if(reader.ReadUInt16()!=0x5A4D) return null;
        stream.Position=60; var pe=reader.ReadInt32();
        if(pe<64 || pe>stream.Length-24) return null;
        stream.Position=pe; if(reader.ReadUInt32()!=0x4550) return null;
        stream.Position=pe+20; var headerSize=reader.ReadUInt16();
        var optional=pe+24L;
        if(optional+headerSize>stream.Length || headerSize<2) return null;
        stream.Position=optional; var magic=reader.ReadUInt16();
        var entry=optional+(magic==0x20B ? 112 : magic==0x10B ? 96 : -1000)+32;
        if(entry<optional || entry+8>optional+headerSize) return null;
        stream.Position=entry; var offset=reader.ReadUInt32(); var size=reader.ReadUInt32();
        return offset>=optional+headerSize && size>=8 && (long)offset+size<=stream.Length ? offset : null;
    }

    /// <summary>The key pinned in the running helper's own executable, or null.</summary>
    public static byte[]? ReadOwn()
    {
        try { return Environment.ProcessPath is { } p ? Read(p).Key : null; }
        catch (IOException) { return null; }
    }

    /// <summary>Rewrites the stamp of a file in place (no-op when it already carries this key). False if the file is locked.</summary>
    public static bool StampInPlace(string path, byte[] spki)
    {
        try
        {
            var (existing, original) = Read(path);
            if (existing is not null && existing.AsSpan().SequenceEqual(spki)) return true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            // Stamp BEFORE Authenticode signing. Never mutate a file carrying a certificate table.
            if (CertificateOffset(fs) is not null) return false;
            fs.SetLength(original);
            fs.Seek(original, SeekOrigin.Begin);
            fs.Write(Tail(spki));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
