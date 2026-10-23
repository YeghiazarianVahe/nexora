namespace Nexora.Domain;

/// <summary>Assignable roles. SuperAdmin is deliberately absent: it is configured, never assigned.</summary>
public enum Role
{
    Viewer = 1,
    Operator = 2,
    Admin = 3,
}

public enum AccessRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>Role names as they appear in claims and API payloads.</summary>
public static class NexoraRoles
{
    public const string None = "None";
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
    public const string SuperAdmin = "SuperAdmin";
}
