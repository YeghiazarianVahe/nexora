namespace Nexora.Domain;

/// <summary>Overall state of a job. Derived from its targets once they are all finished.</summary>
public enum JobState
{
    Queued = 0,
    /// <summary>Waiting for the initiating operator's helper to connect (Nexora never acts with its own identity).</summary>
    WaitingForHelper = 1,
    Running = 2,
    Succeeded = 3,
    Warning = 4,
    PartiallyFailed = 5,
    Failed = 6,
    Cancelled = 7,
}

public enum TargetState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Warning = 3,
    Failed = 4,
    Cancelled = 5,
    TimedOut = 6,
    Skipped = 7,
}

/// <summary>A request to run one catalog action on one or more inventory endpoints. Immutable once created, except for state.</summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public string ActionCode { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string InitiatorSid { get; set; } = "";
    public string InitiatorAccount { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public JobState Status { get; set; } = JobState.Queued;
    /// <summary>Free text, required for High and Critical actions.</summary>
    public string? Reason { get; set; }
    public bool ProtectedOverride { get; set; }
    public string? ClientIp { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int TargetCount { get; set; }
    /// <summary>Set when the operator (or an Admin) pressed Cancel; the orchestrator then stops what is still running.</summary>
    public DateTime? CancelRequestedUtc { get; set; }
    /// <summary>Shared by the jobs created together by one batch request (for example a profile deletion batch); null for ordinary jobs.</summary>
    public Guid? BatchId { get; set; }
}

public sealed class JobTarget
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid EndpointId { get; set; }
    /// <summary>Name and address as they were when the job was created, so history survives endpoint changes.</summary>
    public string EndpointName { get; set; } = "";
    public string Host { get; set; } = "";
    public TargetState Status { get; set; } = TargetState.Queued;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int? ExitCode { get; set; }
    public string? ResultSummary { get; set; }
    /// <summary>Why a target was skipped (offline, protected, busy...).</summary>
    public string? SkipReason { get; set; }
    public int? Progress { get; set; }
    /// <summary>Path of a produced file on the server (for example a diagnostics bundle); null for plain output.</summary>
    public string? OutputPath { get; set; }
    public string? OutputSha256 { get; set; }
    public int LogLines { get; set; }
}

/// <summary>One streamed line of output. Secrets are redacted before storing.</summary>
public sealed class JobLogChunk
{
    public long Id { get; set; }
    public Guid JobTargetId { get; set; }
    public int Seq { get; set; }
    public DateTime TimestampUtc { get; set; }
    /// <summary>stdout, stderr or system.</summary>
    public string Stream { get; set; } = "stdout";
    public string Text { get; set; } = "";
}
