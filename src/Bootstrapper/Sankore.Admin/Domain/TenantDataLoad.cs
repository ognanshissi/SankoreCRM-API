namespace Sankore.Admin.Domain;

/// <summary>Which starting data an institution receives (owner, 2026-11-08).</summary>
public enum TenantDataLoadKind
{
    /// <summary>Configuration only — agencies, products, lead sources, templates — for a real institution.</summary>
    Starter,

    /// <summary>The starter, plus fictitious accounts of every profile and fictitious records. Test institutions only.</summary>
    Demo,
}

public enum TenantDataLoadStatus { Pending, Running, Completed, Failed }

/// <summary>
/// A platform administrator asked to load an institution's starting data
/// (<c>docs/donnees-demarrage-demo.md</c>). Same shape and direction as <see cref="TenantDataWipe"/>:
/// the registry records the request, the API pulls it with its service key, loads, and reports what
/// it created or why it failed. The row is the trace of who loaded what, when.
/// </summary>
public sealed class TenantDataLoad
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public TenantDataLoadKind Kind { get; private set; }

    /// <summary>The platform administrator; <see cref="Guid.Empty"/> when the service key created the tenant.</summary>
    public Guid RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }
    public TenantDataLoadStatus Status { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>What was created, per module — written by the API.</summary>
    public string? Summary { get; private set; }

    public string? Error { get; private set; }

    private TenantDataLoad() { }

    public static TenantDataLoad Request(Guid tenantId, TenantDataLoadKind kind, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Kind = kind,
        RequestedBy = requestedBy,
        RequestedAt = now,
        Status = TenantDataLoadStatus.Pending,
    };

    // Started, completed and failed by conditional UPDATEs in the endpoints, as for a wipe.
}
