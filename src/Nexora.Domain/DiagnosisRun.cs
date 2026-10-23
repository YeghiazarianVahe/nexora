namespace Nexora.Domain;

/// <summary>
/// One run of the deterministic diagnosis engine against one workstation (F5). Created when the operator starts it, finished when the
/// diag.collect job reaches a final state. Deleted with its endpoint (cascade) and together with its job by the retention purge.
/// </summary>
public sealed class DiagnosisRun
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public Guid JobId { get; set; }
    public string ProfileId { get; set; } = "";
    public int ProfileVersion { get; set; }
    public string InitiatedBySid { get; set; } = "";
    /// <summary>Running, Complete, Incomplete or Failed.</summary>
    public string Status { get; set; } = "Running";
    public bool Complete { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>The DiagnosisResult (findings, collector statuses, not-evaluated rules) as JSON; null while the run is still going.</summary>
    public string? ResultJson { get; set; }
    /// <summary>The raw collector outputs the result was derived from ({ "collector-id": {...} }), capped at 200 KB. Operator-readable only.</summary>
    public string? EvidenceJson { get; set; }
    public int SchemaVersion { get; set; } = 1;
}
