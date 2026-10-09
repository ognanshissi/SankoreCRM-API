namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One pass of the daily comparison (INT-34). Kept as a row rather than only logged so the
/// control function can show that the comparison ran every day, which is the part an inspection
/// asks about.
/// </summary>
public sealed class IntegrationReconciliationRun : AggregateRoot
{
    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Null while the run is in flight, or if it crashed — which is itself a finding.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    public int CheckedCount { get; private set; }

    public int GapCount { get; private set; }

    /// <summary>Gaps that disappeared and were closed automatically by this run.</summary>
    public int ClosedCount { get; private set; }

    public string? FailureDetail { get; private set; }

    private IntegrationReconciliationRun() { }

    public static IntegrationReconciliationRun Start(
        Guid tenantId, Guid connectionId, TimeProvider clock, Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");

        return new IntegrationReconciliationRun
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            StartedAt = clock.GetUtcNow(),
        };
    }

    public void Finish(int checkedCount, int gapCount, int closedCount, TimeProvider clock)
    {
        CheckedCount = checkedCount;
        GapCount = gapCount;
        ClosedCount = closedCount;
        FinishedAt = clock.GetUtcNow();
    }

    public void Fail(string detail, TimeProvider clock)
    {
        FailureDetail = detail.Length <= 1000 ? detail : detail[..1000];
        FinishedAt = clock.GetUtcNow();
    }
}

/// <summary>
/// One discrepancy between the CRM and an external system (INT-34).
///
/// <para>
/// A gap that is still true tomorrow is NOT recorded twice: the run matches on
/// <c>(tenant, type, crm_id, external_id)</c> among the open ones and leaves the existing row
/// alone. Otherwise a single unresolved gap becomes three hundred rows in a year and the report
/// stops being readable — which is the same thing as not having one.
/// </para>
/// </summary>
public sealed class IntegrationReconciliationGap : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The run that FIRST saw it. Deliberately not updated by later runs.</summary>
    public Guid RunId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public GapType GapType { get; private set; }

    /// <summary>Null for <see cref="GapType.MissingInCrm"/>: there is no CRM row to point at.</summary>
    public Guid? CrmId { get; private set; }

    /// <summary>Null for <see cref="GapType.MissingInExternal"/>.</summary>
    public string? ExternalId { get; private set; }

    /// <summary>
    /// What differs, as jsonb. Carries compared VALUES (a KYC tier, an active flag), never
    /// identity data: a reconciliation report is routinely exported and mailed.
    /// </summary>
    public string? DetailsJson { get; private set; }

    public GapResolution Resolution { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>What the operator did about it. Required when resolving by hand.</summary>
    public string? ResolutionNote { get; private set; }

    public DateTimeOffset DetectedAt { get; private set; }

    /// <summary>Last run that still saw it — how long it has been open.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    public uint Version { get; private set; }

    private IntegrationReconciliationGap() { }

    public static IntegrationReconciliationGap Open(
        Guid tenantId,
        Guid runId,
        Guid connectionId,
        GapType gapType,
        Guid? crmId,
        string? externalId,
        TimeProvider clock,
        string? detailsJson = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (runId == Guid.Empty) throw new DomainException("RunId is required.");

        if (crmId is null && string.IsNullOrWhiteSpace(externalId))
            throw new DomainException("A gap needs at least one side to point at.");

        var now = clock.GetUtcNow();

        return new IntegrationReconciliationGap
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            RunId = runId,
            ConnectionId = connectionId,
            GapType = gapType,
            CrmId = crmId,
            ExternalId = externalId?.Trim(),
            DetailsJson = detailsJson,
            Resolution = GapResolution.Open,
            DetectedAt = now,
            LastSeenAt = now,
        };
    }

    /// <summary>Seen again by a later run. Keeps DetectedAt, which is the age of the problem.</summary>
    public void Touch(TimeProvider clock) => LastSeenAt = clock.GetUtcNow();

    /// <summary>
    /// A human acted on it. The note is mandatory: an untraceable resolution of a compliance
    /// finding is the one failure mode this table exists to prevent.
    /// </summary>
    public Result Resolve(Guid actor, string note, TimeProvider clock)
    {
        if (Resolution != GapResolution.Open)
            return Result.Fail(PublicApi.IntegrationErrors.GapAlreadyResolved);

        if (string.IsNullOrWhiteSpace(note))
            return Result.Fail(PublicApi.IntegrationErrors.PayloadInvalid);

        Resolution = GapResolution.Resolved;
        ResolvedBy = actor;
        ResolutionNote = note.Trim();
        ResolvedAt = clock.GetUtcNow();
        return Result.Ok();
    }

    /// <summary>
    /// Gone on its own: the next run no longer found it. Closed by the SYSTEM, with no note and
    /// no actor — nobody decided it, it stopped being true.
    /// </summary>
    public void CloseAutomatically(TimeProvider clock)
    {
        if (Resolution != GapResolution.Open) return;

        Resolution = GapResolution.Closed;
        ResolvedAt = clock.GetUtcNow();
    }
}
