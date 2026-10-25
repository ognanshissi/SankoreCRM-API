namespace Sankore.Admin.Domain;

public enum TenantDataWipeStatus { Pending, Running, Completed, Failed }

/// <summary>
/// A platform administrator asked to empty a TEST institution of its business data (data-purge
/// lot 4): leads, clients, projects, KYC files, calls, conversations… Its configuration — settings,
/// forms, catalogue, roles, users, agencies, workflows, templates — stays, so the next recette
/// starts from a configured institution and an empty base.
///
/// <para>
/// The registry only RECORDS the request: the data lives in the API's database, and the API pulls
/// pending wipes (the same direction as everything else between the two — the API reads the
/// registry, never the reverse), checks again that the institution is a test one, wipes, and
/// reports here. The row is the trace of who emptied what, when, and what it removed.
/// </para>
/// </summary>
public sealed class TenantDataWipe
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public TenantDataWipeStatus Status { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>What was removed, per module — written by the API.</summary>
    public string? Summary { get; private set; }

    public string? Error { get; private set; }

    private TenantDataWipe() { }

    public static TenantDataWipe Request(Guid tenantId, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        RequestedBy = requestedBy,
        RequestedAt = now,
        Status = TenantDataWipeStatus.Pending,
    };

    // Started, completed and failed by conditional UPDATEs in the endpoints (Pending → Running →
    // Completed | Failed): two API instances cannot both take a wipe, and a finished one is never
    // rewritten.
}
