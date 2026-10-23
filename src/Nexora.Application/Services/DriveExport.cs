using System.Globalization;

namespace Nexora.Application.Services;

/// <summary>A physical drive as the export sees it. Type comes from <see cref="DriveClassifier"/>, the same as the table.</summary>
public sealed record ExportDrive(string Model, string? Serial, string Type, int? HealthPercent, int? TempC, long? PowerOnHours, long SizeBytes);

/// <summary>One CSV row: the workstation as the list shows it, plus its stored drives.</summary>
public sealed record ExportRow(EndpointSummaryDto E, IReadOnlyList<ExportDrive> Drives);

public sealed record DriveSlot(ExportDrive Drive, IReadOnlyList<VolumeDto> Volumes);

public sealed record DriveLayout(IReadOnlyList<DriveSlot> Slots, IReadOnlyList<VolumeDto> Unassigned);

/// <summary>Which of a PC's volumes sit on its SSDs and which on its HDDs, and how space is written in the export.</summary>
public static class DriveExport
{
    /// <summary>
    /// The scan does not store which volume lives on which drive, so it is inferred: the system volume (C:) lives on the smallest drive that can
    /// hold it, and with exactly one other drive the remaining volumes live on that one. A PC with three or more drives and extra volumes cannot
    /// be told apart; those volumes stay unassigned (and are left out of the SSD and HDD space) instead of being guessed.
    /// </summary>
    public static DriveLayout Layout(IReadOnlyList<ExportDrive> drives, IReadOnlyList<VolumeDto>? volumes)
    {
        var vols = volumes ?? [];
        if (drives.Count == 0) return new DriveLayout([], vols);

        var sys = SystemVolume.Of(vols);
        ExportDrive? system = drives.Count == 1 ? drives[0]
            : sys is null ? null
            : drives.Where(d => d.SizeBytes >= sys.TotalBytes).OrderBy(d => d.SizeBytes).ThenBy(d => d.Model, StringComparer.Ordinal).ThenBy(d => d.Serial, StringComparer.Ordinal).FirstOrDefault();

        var ordered = drives
            .OrderByDescending(d => ReferenceEquals(d, system))
            .ThenByDescending(d => d.SizeBytes).ThenBy(d => d.Model, StringComparer.Ordinal).ThenBy(d => d.Serial, StringComparer.Ordinal)
            .ToList();

        var onDrive = new Dictionary<ExportDrive, List<VolumeDto>>(ReferenceEqualityComparer.Instance);
        var unassigned = new List<VolumeDto>();
        if (drives.Count == 1) onDrive[drives[0]] = [.. vols];
        else if (system is not null && sys is not null)
        {
            onDrive[system] = [sys];
            var rest = vols.Where(v => !ReferenceEquals(v, sys)).ToList();
            var others = ordered.Where(d => !ReferenceEquals(d, system)).ToList();
            if (others.Count == 1) onDrive[others[0]] = rest;
            else unassigned.AddRange(rest);
        }
        else unassigned.AddRange(vols);

        return new DriveLayout(ordered.Select(d => new DriveSlot(d, onDrive.GetValueOrDefault(d) ?? [])).ToList(), unassigned);
    }

    /// <summary>
    /// Free and total space of every volume that lives on a drive of this type ("SSD" or "HDD"), added together when the PC has several such
    /// drives. Null when the PC has no such drive or none of its volumes can be placed on one.
    /// </summary>
    public static (long Free, long Total)? Space(DriveLayout layout, string type)
    {
        var vols = layout.Slots.Where(s => s.Drive.Type == type).SelectMany(s => s.Volumes).ToList();
        return vols.Count == 0 ? null : (vols.Sum(v => v.FreeBytes), vols.Sum(v => v.TotalBytes));
    }

    /// <summary>"316.5 / 475.1 GB": free / total with one decimal, in GB (1 GB = 2^30 bytes, as Windows shows it) or TB from 1 TB up, by the total.</summary>
    public static string FormatSpace(long freeBytes, long totalBytes)
    {
        const double gb = 1073741824.0, tb = gb * 1024;
        var unit = totalBytes >= tb ? tb : gb;
        return string.Create(CultureInfo.InvariantCulture, $"{freeBytes / unit:0.0} / {totalBytes / unit:0.0} {(unit == tb ? "TB" : "GB")}");
    }
}
