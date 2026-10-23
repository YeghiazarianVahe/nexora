using System.Globalization;

namespace Nexora.Application.Services;

/// <summary>One line of the health breakdown: what was found and how many points it cost (Delta is 0 for a note, negative for a penalty, positive for points given back by a floor or the ADATA cap). The deltas of a drive always add up to score minus 100.</summary>
public sealed record HealthReason(string Code, string Text, int Delta)
{
    public override string ToString() => Delta < 0 ? $"{Text.TrimEnd('.')} (−{-Delta})" : Text;
}

/// <summary>
/// The temperature of one drive, judged on its own (never part of the health score). <c>Source</c> is "drive" when the warning/critical limits
/// are the drive's own (NVMe WCTEMP/CCTEMP) and "default" when they are the Admin-set limits for the drive class.
/// </summary>
public sealed record TemperatureInfo(int? CurrentC, int? WarnC, int? CritC, string Source, int? LifetimeMaxC, string? Status);

public sealed record DiskHealthResult(
    int? HealthPercent, string Status, string Summary, int? EstimatedDaysLeft,
    IReadOnlyList<HealthReason>? Reasons = null, int? TempC = null, string? TempStatus = null,
    TemperatureInfo? Temperature = null, string? InterfaceWarning = null);

/// <summary>Default temperature limits per drive class (Admin Settings, <see cref="PlatformSettings"/>); a limit the drive reports itself takes precedence.</summary>
public sealed record DriveTempLimits(int HddWarnC = 50, int HddCritC = 55, int SsdWarnC = 60, int SsdCritC = 70, int NvmeWarnC = 70, int NvmeCritC = 80)
{
    public static DriveTempLimits Default { get; } = new();
}

/// <summary>
/// Every number the health model uses, in one object so a weight change is a configuration change (section <c>DiskHealth</c>), not a code change.
/// Raw attributes are stored, so new weights apply to the next score without a re-scan.
/// Score version 5: independent SMART/NVMe heuristic with evidence validation; see docs/storage-health-v5.md. No proprietary algorithm.
/// </summary>
public sealed class HealthWeights
{
    /// <summary>smart-raw uses the published default HDD raw-attribute weights and multiplicative calculation.</summary>
    public string HddMethod { get; set; } = "independent";
    /// <summary>Retain the pre-existing critical-sector severity policy independently of published percentage weights.</summary>
    public bool HddCriticalSectorStatus { get; set; } = true;
    /// <summary>ADATA attribute 169 raw as remaining life. Vendor-specific and always low confidence; can be disabled.</summary>
    public bool Adata169Enabled { get; set; } = true;

    /// <summary>"count:penalty" steps; the highest step whose count is reached applies.</summary>
    public string ReallocatedTiers { get; set; } = "1:10,10:20,100:35,500:45";

    // Count-based penalties are concave: Base at the first occurrence, then Scale * ln(count), never above Max.
    public int PendingBase { get; set; } = 15;
    public double PendingScale { get; set; } = 6.2;
    public int PendingMax { get; set; } = 40;
    public int UncorrectableBase { get; set; } = 15;
    public double UncorrectableScale { get; set; } = 6.2;
    public int UncorrectableMax { get; set; } = 40;

    /// <summary>A spinning drive with this many pending sectors, or this many uncorrectable ones, is Critical whatever else is true.</summary>
    public int HddCriticalPending { get; set; } = 6;
    public int HddCriticalUncorrectable { get; set; } = 1;
    public int CriticalScoreCap { get; set; } = 20;

    // secondary HDD signals
    public int ReallocEventBase { get; set; } = 3;
    public double ReallocEventScale { get; set; } = 2.5;
    public int ReallocEventMax { get; set; } = 10;
    public int SpinRetryBase { get; set; } = 5;
    public double SpinRetryScale { get; set; } = 4;
    public int SpinRetryMax { get; set; } = 15;
    /// <summary>Command timeouts up to this many are normal background noise on a healthy drive and cost nothing.</summary>
    public int CommandTimeoutTolerated { get; set; } = 100;
    public int CommandTimeoutMax { get; set; } = 10;
    public int ReportedUncorrectMax { get; set; } = 25;

    // SSD
    public int ProgramEraseFailMax { get; set; } = 30;
    public int MediaErrorBase { get; set; } = 5;
    public double MediaErrorScale { get; set; } = 4;
    public int MediaErrorMax { get; set; } = 25;

    // NVMe
    public int NvmeSpareMargin { get; set; } = 5;
    public int NvmeSpareMarginPenalty { get; set; } = 5;

    // interface errors (SMART 199): reported as a connection warning, never part of the score
    public int CrcTolerated { get; set; } = 10;
    public int IoErrorMax { get; set; } = 25;

    public static HealthWeights Default { get; } = new();

    public (long From, int Penalty)[] ParseReallocatedTiers() =>
        ReallocatedTiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split(':'))
            .Where(p => p.Length == 2 && long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                        && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(p => (long.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)))
            .OrderBy(t => t.Item1).ToArray();

    /// <summary>The concave penalty for <paramref name="count"/> (at least 1) occurrences.</summary>
    public static int Graded(long count, int baseHit, double scale, int max) =>
        (int)Math.Min(max, Math.Round(baseHit + scale * Math.Log(Math.Max(1, count)), MidpointRounding.AwayFromZero));
}

/// <summary>Temperature judged on its own: the drive's own limits when it reports valid ones, otherwise the class defaults.</summary>
public static class DriveTemperature
{
    public const string SourceDrive = "drive", SourceDefault = "default";

    public static TemperatureInfo Resolve(DiskRaw d, DriveTempLimits limits, int? currentC, bool driveFlaggedTemperature = false)
    {
        currentC = Valid(currentC);
        var nvme = d.MediaType == "NVMe" || string.Equals(d.BusType, "NVMe", StringComparison.OrdinalIgnoreCase);
        var (warn, crit) = nvme ? (limits.NvmeWarnC, limits.NvmeCritC) : d.MediaType == "SSD" ? (limits.SsdWarnC, limits.SsdCritC) : (limits.HddWarnC, limits.HddCritC);
        var source = SourceDefault;
        // A drive's own limits count only when both are present and sensible; a half-reported or contradictory pair is ignored as a whole.
        if (d.DriveTempWarnC is { } dw && d.DriveTempCritC is { } dc && dw is > 0 and < 150 && dc is > 0 and < 150 && dw < dc)
        {
            (warn, crit) = (dw, dc);
            source = SourceDrive;
        }

        // NVMe offers no lifetime maximum in its health log: only a value the collector explicitly provided is shown.
        int? max = nvme ? d.LifetimeMaxTempC : d.LifetimeMaxTempC ?? SmartParser.LifetimeMaxTemperature(d.Attributes) ?? d.MaxTemperatureC;
        if (max is <= 0 or >= 150) max = null;
        if (max is { } m && currentC is { } c && m < c) max = c;   // a lifetime maximum cannot be below the current reading

        string? status = currentC is not { } t ? null : t >= crit ? DiskStatus.Critical : t >= warn ? DiskStatus.Warning : DiskStatus.Ok;
        if (driveFlaggedTemperature && status == DiskStatus.Ok) status = DiskStatus.Warning;   // the drive itself flagged a temperature problem
        return new TemperatureInfo(currentC, warn, crit, source, max, status);
    }

    public static int? Valid(int? value) => value is > 0 and < 120 ? value : null;
}

/// <summary>
/// Turns raw SMART / reliability data into an independent 0-100 heuristic estimate, a status and an evidence ledger.
/// This is not a proprietary implementation or an accuracy guarantee. Temperature is judged separately.
/// Pure and deterministic so every rule is unit-tested.
/// </summary>
public static class DiskHealthEvaluator
{
    /// <summary>Bumped whenever the scoring changes, so every stored score is recomputed by a fresh scan.</summary>
    public const int ScoreVersion = 7;

    /// <summary>NVMe critical-warning bits that mean the drive is failing: 0 spare, 2 reliability, 3 read-only, 4 volatile backup, 5 persistent memory. Bit 1 (temperature) is judged with the temperature.</summary>
    private const int NvmeHealthWarningMask = 0x3D;

    public static bool HasNvmeHealthWarning(int flags) => (flags & NvmeHealthWarningMask) != 0;

    public static DiskHealthResult Evaluate(DiskRaw d, HealthWeights? weights = null, DriveTempLimits? limits = null)
    {
        var w = weights ?? HealthWeights.Default;
        var ssd = DriveClassifier.Classify(d.MediaType, d.BusType) == DriveClassifier.Ssd;
        // Reject invalid endurance fields as a whole; do not clamp corrupt telemetry into a healthy drive.
        if (d.Nvme is { } check && (check.PercentageUsed is < 0 or > 255 || check.AvailableSpare is < 0 or > 100
            || check.SpareThreshold is < 0 or > 100 || check.CriticalWarning is < 0 or > 255 || check.MediaErrors < 0))
            return new DiskHealthResult(null, DiskStatus.Unknown, "Unavailable: inconsistent NVMe health log. Refresh the drive scan.", null,
                [new HealthReason("invalid-nvme", "NVMe fields are outside their standard ranges.", 0)]);
        var attrs = d.Attributes;
        var tempC = DriveTemperature.Valid(d.TemperatureC) ?? SmartParser.Temperature(attrs);
        var temperature = DriveTemperature.Resolve(d, limits ?? DriveTempLimits.Default, tempC, d.Nvme is { } tn && (tn.CriticalWarning & 0x02) != 0);
        var crc = d.CrcErrors ?? SmartParser.Raw(attrs, 199);
        var interfaceWarning = crc is > 0
            ? $"{crc} interface (CRC) error{(crc == 1 ? "" : "s")}; check the cable or port."
            : null;

        var hasData = d.HealthStatus is "Warning" or "Unhealthy" || d.PredictFailure || d.Nvme is not null
                      || d.WearPercent is > 0 and <= 100 || (d.WearPercent == 0 && !(d.MediaType == "NVMe" || string.Equals(d.BusType, "NVMe", StringComparison.OrdinalIgnoreCase)))
                      || d.LifeLeftPercent is >= 0 and <= 100
                      || d.Reallocated is >= 0 || d.Pending is >= 0 || d.OfflineUncorrectable is >= 0
                      || d.ReadErrors is > 0 || d.WriteErrors is > 0
                      || attrs.Any(a => a.Id is 5 or 10 or 171 or 172 or 181 or 182 or 187 or 188 or 196 or 197 or 198
                          || (a.Threshold > 0 && a.Id is not (190 or 194)))
                      || SmartParser.LifeLeftWithSource(attrs, ssd, d.Model, w.Adata169Enabled) is not null;
        if (!hasData)
            return new DiskHealthResult(null, DiskStatus.Unknown,
                "Unavailable: insufficient endurance or fault evidence to estimate health. Windows status, temperature and power-on time alone do not measure remaining life.", null,
                [new HealthReason("na", "No usable endurance or fault evidence was reported.", 0)], tempC, temperature.Status, temperature, interfaceWarning);

        if (DriveClassifier.Classify(d.MediaType, d.BusType) == DriveClassifier.Hdd
            && w.HddMethod == "smart-raw" && attrs.Any(a => a.Id is 5 or 7 or 10 or 196 or 197 or 198))
            return EvaluateHddRaw(d, w, tempC, temperature, interfaceWarning);

        var reasons = new List<HealthReason>();
        var cap = 100;
        var critical = false;
        var penalty = 0;

        void Cap(string code, int to, string text, bool crit)
        {
            if (crit) critical = true;
            var lost = cap - Math.Min(cap, to);
            cap -= lost;
            reasons.Add(new HealthReason(code, text, -lost));
        }
        void Pen(string code, int points, string text)
        {
            penalty += points;
            reasons.Add(new HealthReason(code, text, -points));
        }

        if (d.HealthStatus == "Unhealthy") Cap("windows", 20, "Windows reports this drive as unhealthy.", true);
        else if (d.HealthStatus == "Warning") Cap("windows", 60, "Windows reports a health warning for this drive.", false);
        if (d.PredictFailure) Cap("predict", 10, "SMART predicts imminent failure. Back up the data and replace the drive.", true);

        // ---- wear / remaining life
        if (d.Nvme is { } nv)
        {
            reasons.Add(new HealthReason("nvme-source", "Standard NVMe endurance log; final health is a heuristic estimate, not a measured life guarantee.", 0));
            if (d.WearPercent is >= 0 and <= 100 and var alternate && Math.Abs(alternate - Math.Min(100, nv.PercentageUsed)) > 10)
                reasons.Add(new HealthReason("telemetry-conflict", "Windows wear differs from the standard NVMe log; NVMe percentage used takes precedence. Verify the source.", 0));
            // NVMe: health = 100 - percentage used (the drive's own endurance estimate), then its fault indicators.
            var used = Math.Clamp(nv.PercentageUsed, 0, 100);
            if (used > 0) Pen("nvme-used", used, $"NVMe percentage used: {used}%.");

            if (nv.AvailableSpare < nv.SpareThreshold)
                Cap("nvme-spare", w.CriticalScoreCap, $"Available spare {nv.AvailableSpare}% is below the drive's threshold ({nv.SpareThreshold}%).", true);
            else if (nv.AvailableSpare <= nv.SpareThreshold + w.NvmeSpareMargin)
                Pen("nvme-spare", w.NvmeSpareMarginPenalty, $"Available spare {nv.AvailableSpare}% is close to the drive's threshold ({nv.SpareThreshold}%).");

            if (HasNvmeHealthWarning(nv.CriticalWarning))
                Cap("nvme-warning", w.CriticalScoreCap, $"The drive raised a critical warning (flags 0x{nv.CriticalWarning:X2}: {NvmeWarningText(nv.CriticalWarning)}).", true);
            if ((nv.CriticalWarning & 0x02) != 0)
                reasons.Add(new HealthReason("nvme-warning-temp", "The drive flagged a temperature problem (shown under temperature, not in the health score).", 0));
            if (nv.MediaErrors > 0)
                Pen("nvme-media", HealthWeights.Graded(nv.MediaErrors, w.MediaErrorBase, w.MediaErrorScale, w.MediaErrorMax), $"{nv.MediaErrors} media/data-integrity errors.");
        }
        else
        {
            // Recomputed from the stored attributes, so the ADATA switch applies without a re-scan.
            var lifeAttr = attrs.Count > 0 ? SmartParser.LifeLeftWithSource(attrs, ssd, d.Model, w.Adata169Enabled) : null;
            int? life = lifeAttr?.Value ?? (attrs.Count > 0 ? null : d.LifeLeftPercent is >= 0 and <= 100 ? d.LifeLeftPercent : null);
            life ??= d.WearPercent is >= 0 and <= 100 and var wp ? 100 - wp : null;
            if (life is { } reportedLife && d.WearPercent is >= 0 and <= 100 and var windowsWear && Math.Abs(reportedLife - (100 - windowsWear)) > 10)
            {
                reasons.Add(new HealthReason("telemetry-conflict", "SMART remaining life and Windows wear disagree; using the lower estimate pending a fresh scan.", 0));
                life = Math.Min(reportedLife, 100 - windowsWear);
            }
            if (life is { } l)
            {
                l = Math.Clamp(l, 0, 100);
                var text = lifeAttr is { } la && la.Value == l ? $"Remaining life is {l}% (SMART attribute {la.AttributeId})." : $"Remaining life is {l}%.";
                Cap("life", l, text, false);
                if (l >= 50) reasons.RemoveAll(r => r.Code == "life" && r.Delta == 0);
            }
        }

        // ---- sector / media faults
        if (d.Reallocated is > 0)
        {
            var tier = w.ParseReallocatedTiers().LastOrDefault(t => d.Reallocated >= t.From);
            if (tier.Penalty > 0) Pen("5", tier.Penalty, $"Reallocated sectors: {d.Reallocated}.");
        }
        if (d.Pending is > 0)
            Pen("197", HealthWeights.Graded(d.Pending.Value, w.PendingBase, w.PendingScale, w.PendingMax), $"Pending sectors: {d.Pending} (waiting to be reallocated).");
        if (d.OfflineUncorrectable is > 0)
            Pen("198", HealthWeights.Graded(d.OfflineUncorrectable.Value, w.UncorrectableBase, w.UncorrectableScale, w.UncorrectableMax), $"Uncorrectable sectors: {d.OfflineUncorrectable}.");

        if (!ssd && (d.Pending >= w.HddCriticalPending || d.OfflineUncorrectable >= w.HddCriticalUncorrectable))
            Cap("floor", w.CriticalScoreCap, "Unstable or uncorrectable sectors on a spinning drive: the drive is failing.", true);

        // SMART 199 is an interface (cable / connection) problem, not a sign of a failing drive: it is reported, never scored.
        if (crc is > 0)
        {
            var note = crc > w.CrcTolerated ? interfaceWarning! : $"{interfaceWarning!.TrimEnd('.')} (a few are usually a past cable glitch).";
            reasons.Add(new HealthReason("199", note, 0));
        }
        var ioErrors = (d.ReadErrors ?? 0) + (d.WriteErrors ?? 0);
        if (ioErrors > 0) Pen("io", (int)Math.Min(w.IoErrorMax, 5 + ioErrors), $"{ioErrors} uncorrected read/write errors.");

        long Raw(int id) => SmartParser.Raw(attrs, id) ?? 0;
        if (ioErrors == 0 && Raw(187) > 0)
            Pen("187", (int)Math.Min(w.ReportedUncorrectMax, 5 + Raw(187)), $"Reported uncorrectable errors: {Raw(187)}.");

        if (!ssd)
        {
            if (Raw(196) > 0) Pen("196", HealthWeights.Graded(Raw(196), w.ReallocEventBase, w.ReallocEventScale, w.ReallocEventMax), $"Reallocation events: {Raw(196)}.");
            if (Raw(10) > 0) Pen("10", HealthWeights.Graded(Raw(10), w.SpinRetryBase, w.SpinRetryScale, w.SpinRetryMax), $"Spin retries: {Raw(10)}.");
            var timeouts = Raw(188) & 0xFFFF; // Seagate packs several counters into this value; the low word is the timeout count
            if (timeouts > w.CommandTimeoutTolerated)
                Pen("188", (int)Math.Round(Math.Min(w.CommandTimeoutMax, 2 + timeouts / 100.0), MidpointRounding.AwayFromZero), $"Command timeouts: {timeouts}.");
        }
        else
        {
            var pe = Raw(171) + Raw(172) + Raw(181) + Raw(182);
            if (pe > 0) Pen("171/172", (int)Math.Min(w.ProgramEraseFailMax, 10 + 2 * pe), $"Program/erase failures: {pe}.");
        }

        foreach (var a in attrs.Where(a => a.Id is not (190 or 194) && a.Threshold > 0 && a.Current <= a.Threshold))
        {
            critical = true;
            reasons.Add(new HealthReason(a.Id.ToString(CultureInfo.InvariantCulture), $"SMART attribute {a.Name} ({a.Id}) has reached its failure threshold.", 0));
        }

        // A drive can still answer after exhausting rated endurance. Do not invent a positive life floor.
        var unclamped = cap - penalty;
        var health = Math.Clamp(unclamped, 0, 100);
        if (health != unclamped)
            reasons.Add(new HealthReason("floor-min", "Health is bounded to 0-100%.", health - unclamped));

        var status = critical || health < DiskStatus.CriticalBelow ? DiskStatus.Critical : health < DiskStatus.OkFrom ? DiskStatus.Warning : DiskStatus.Ok;

        if (attrs.Count == 0 && d.Nvme is null && reasons.Count == 0)
            reasons.Add(new HealthReason("reliability-source", "Estimated from reported endurance or fault counters; no SMART attributes were available.", 0));

        int? daysLeft = null;
        if (ssd && d.WearPercent is > 0 and < 100 && d.PowerOnHours is > 0)
        {
            var daysPerPercent = d.PowerOnHours.Value / 24.0 / d.WearPercent.Value;
            daysLeft = (int)Math.Min(36_500, daysPerPercent * (100 - d.WearPercent.Value));
        }

        var summary = reasons.Count == 0 ? "No problems found. The drive is in good condition." : string.Join("\n", reasons.Select(r => r.ToString()));
        return new DiskHealthResult(health, status, summary, daysLeft, reasons, tempC, temperature.Status, temperature, interfaceWarning);
    }

    private static DiskHealthResult EvaluateHddRaw(DiskRaw d, HealthWeights weights, int? tempC, TemperatureInfo temperature, string? interfaceWarning)
    {
        // https://kb.hdsentinel.com/help/en/52_cond.html : default method,
        // lower 16 bits of raw counts, capped degradation per attribute, multiplicative remaining health.
        var rules = new (int Id, double Weight, int Limit)[] { (5, 1, 70), (7, .5, 20), (10, 3, 60), (196, .6, 30), (197, .6, 48), (198, 1, 70) };
        var reasons = new List<HealthReason>();
        double remaining = 100;
        var score = 100;
        foreach (var rule in rules)
        {
            // Seagate's seek-rate raw field counts operations, not failed seeks.
            // Its normalized failure threshold is still checked below.
            if (rule.Id == 7 && (d.Model.StartsWith("ST", StringComparison.OrdinalIgnoreCase)
                || d.Model.StartsWith("Seagate", StringComparison.OrdinalIgnoreCase))) continue;
            var attribute = d.Attributes.FirstOrDefault(a => a.Id == rule.Id);
            if (attribute is null || attribute.Raw < 0) continue;
            var count = attribute.Raw & 0xFFFF;
            if (count == 0) continue;
            remaining *= (100 - Math.Min(rule.Limit, count * rule.Weight)) / 100;
            var next = (int)Math.Floor(remaining + 1e-9);
            reasons.Add(new HealthReason(rule.Id.ToString(CultureInfo.InvariantCulture), $"{attribute.Name}: {count} (SMART {rule.Id}).", next - score));
            score = next;
        }
        var critical = d.PredictFailure || d.HealthStatus == "Unhealthy";
        foreach (var a in d.Attributes.Where(a => a.Id is not (190 or 194) && a.Threshold > 0 && a.Current <= a.Threshold))
        {
            critical = true;
            reasons.Add(new HealthReason("threshold", $"SMART {a.Id} ({a.Name}) reached its failure threshold.", 0));
        }
        if (critical)
        {
            reasons.Add(new HealthReason("failure", "The drive reports failure or a failed SMART threshold. Back up data and replace the drive.", -score));
            score = 0;
        }
        var io = Math.Max(d.ReadErrors ?? 0, d.WriteErrors ?? 0);
        var reported = SmartParser.Raw(d.Attributes, 187) ?? 0;
        var hasAdditionalFault = io > 0 || reported > 0;
        if (hasAdditionalFault)
            reasons.Add(new HealthReason(io > 0 ? "io" : "187", io > 0
                ? $"Uncorrected I/O errors reported (read: {d.ReadErrors?.ToString() ?? "unknown"}, write: {d.WriteErrors?.ToString() ?? "unknown"})."
                : $"Reported uncorrectable errors: {reported} (SMART 187).", 0));
        var pending = Math.Max(d.Pending ?? 0, SmartParser.Raw(d.Attributes, 197) ?? 0);
        var uncorrectable = Math.Max(d.OfflineUncorrectable ?? 0, SmartParser.Raw(d.Attributes, 198) ?? 0);
        if (weights.HddCriticalSectorStatus && (pending >= weights.HddCriticalPending || uncorrectable >= weights.HddCriticalUncorrectable))
        {
            critical = true;
            reasons.Add(new HealthReason("sector-policy", "Critical sector evidence under the existing HDD severity policy; percentage remains the published condition estimate.", 0));
        }
        if (interfaceWarning is not null) reasons.Add(new HealthReason("199", interfaceWarning, 0));
        reasons.Add(new HealthReason("hdd-smart-raw", "HDD condition estimated from raw SMART fault counts using the published default weights; this is not remaining lifetime.", 0));
        var status = critical || score < DiskStatus.CriticalBelow ? DiskStatus.Critical : score < DiskStatus.OkFrom || d.HealthStatus == "Warning" || hasAdditionalFault || pending > 0 || uncorrectable > 0 ? DiskStatus.Warning : DiskStatus.Ok;
        return new DiskHealthResult(score, status, string.Join("\n", reasons.Select(r => r.ToString())), null, reasons, tempC, temperature.Status, temperature, interfaceWarning);
    }

    private static string NvmeWarningText(int flags)
    {
        var parts = new List<string>();
        if ((flags & 0x01) != 0) parts.Add("spare below threshold");
        if ((flags & 0x04) != 0) parts.Add("reliability degraded");
        if ((flags & 0x08) != 0) parts.Add("read-only");
        if ((flags & 0x10) != 0) parts.Add("volatile memory backup failed");
        if ((flags & 0x20) != 0) parts.Add("persistent memory region unreliable");
        return string.Join(", ", parts);
    }

}
