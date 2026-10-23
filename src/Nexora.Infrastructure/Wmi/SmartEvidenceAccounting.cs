using Nexora.Application.Services;

namespace Nexora.Infrastructure.Wmi;
public static class SmartEvidenceAccounting
{
    public sealed record ProviderRecord(string Instance, string Fingerprint);
    public static string Fingerprint(byte[]? bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes ?? []));
    /// <summary>Native keys are only the identity-matched ATA fallbacks actually consumed when WMI had no attributes.
    /// Duplicate provider instances are normalized; genuinely unmatched WMI rows remain counted for the audit.</summary>
    public static int Count(IEnumerable<string> wmiInstances, IEnumerable<string> consumedNativeKeys, IEnumerable<string>? replacedWmi = null)
        => CountProviders(wmiInstances.Select(instance => new ProviderRecord(instance,"")), consumedNativeKeys, replacedWmi);
    public static int CountProviders(IEnumerable<ProviderRecord> wmiInstances, IEnumerable<string> consumedNativeKeys, IEnumerable<string>? replacedWmi = null)
    {
        var replaced=(replacedWmi ?? []).Select(DriveMatching.NormalizePnp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wmiInstances.Select(record => (Instance:DriveMatching.NormalizePnp(record.Instance),record.Fingerprint))
            .Where(k => k.Instance is not null && !replaced.Contains(k.Instance)).Distinct().Count()
            + consumedNativeKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }
}
