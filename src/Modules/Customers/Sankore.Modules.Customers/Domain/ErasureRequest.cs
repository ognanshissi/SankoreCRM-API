namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

public enum ErasureSubjectType { Lead, Client }

public enum ErasureRequestStatus
{
    /// <summary>Created, waiting for a second person to decide.</summary>
    Pending,

    /// <summary>The person's data was erased.</summary>
    Completed,

    /// <summary>
    /// Approved, but the client is still under its legal retention (LBC/FT): the processing is
    /// restricted until then, and the erasure runs by itself once it becomes possible.
    /// </summary>
    Restricted,

    /// <summary>Refused by the second person.</summary>
    Rejected,
}

/// <summary>
/// A person asked to be forgotten (data-purge lot 2, owner's choices of 2026-10-24).
///
/// <para>
/// <b>Two people.</b> One records the request — the reason and what proves the person asked;
/// ANOTHER, holding <c>privacy:erasure:approve</c>, decides. Never the same: an erasure cannot be
/// undone, and a single pair of hands could erase anybody.
/// </para>
///
/// <para>
/// <b>Prospects go, clients wait.</b> A lead whose person never became a client is erased on
/// approval. A client is erased only once its legal retention is over (archived, plus
/// <c>retention-years</c>, KYC released); before, the approval RESTRICTS the processing and a daily
/// job erases it on the day it becomes possible.
/// </para>
///
/// <para>
/// <b>The row is the proof.</b> It never holds the person's name: <see cref="SubjectReference"/> is
/// a client number or a lead's short id, and the reason and evidence must not quote personal data
/// either — they outlive the erasure, as the audit entry does.
/// </para>
/// </summary>
public sealed class ErasureRequest
{
    public const int MaxTextLength = 500;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    public ErasureSubjectType SubjectType { get; private set; }
    public Guid SubjectId { get; private set; }

    /// <summary>Client number, or « Lead 1a2b3c4d » — never the name.</summary>
    public string SubjectReference { get; private set; } = default!;

    /// <summary>The client a lead's person is, when the request named a lead of a client.</summary>
    public Guid? ClientId { get; private set; }

    public string Reason { get; private set; } = default!;

    /// <summary>What proves the person asked: « courrier reçu le 12/10/2026 », a ticket number…</summary>
    public string Evidence { get; private set; } = default!;

    public Guid RequestedBy { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }

    public ErasureRequestStatus Status { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionNote { get; private set; }

    /// <summary>
    /// For a restricted request: when the erasure becomes possible — archive date plus the
    /// retention. Null while the relationship is still active (the date is not known yet).
    /// </summary>
    public DateTimeOffset? EligibleFrom { get; private set; }

    /// <summary>Why the erasure could not run at the decision, as a code (RETENTION_NOT_REACHED…).</summary>
    public string? Blocker { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    private ErasureRequest() { }

    public static ErasureRequest Create(
        Guid tenantId, ErasureSubjectType subjectType, Guid subjectId, string subjectReference, Guid? clientId,
        string reason, string evidence, Guid requestedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        SubjectType = subjectType,
        SubjectId = subjectId,
        SubjectReference = subjectReference,
        ClientId = clientId,
        Reason = reason.Trim(),
        Evidence = evidence.Trim(),
        RequestedBy = requestedBy,
        RequestedAt = now,
        Status = ErasureRequestStatus.Pending,
    };

    /// <summary>The second person's decision is theirs: not the requester's, and only once.</summary>
    public Result CanBeDecidedBy(Guid actor)
    {
        if (Status != ErasureRequestStatus.Pending) return Result.Fail(CustomerErrors.ErasureNotPending);
        if (actor == RequestedBy) return Result.Fail(CustomerErrors.ErasureFourEyes);
        return Result.Ok();
    }

    public void Complete(Guid decidedBy, DateTimeOffset now, string? note)
    {
        if (Status == ErasureRequestStatus.Pending)
        {
            DecidedBy = decidedBy;
            DecidedAt = now;
            DecisionNote = Clean(note);
        }
        Status = ErasureRequestStatus.Completed;
        Blocker = null;
        CompletedAt = now;
    }

    public void Restrict(Guid decidedBy, DateTimeOffset now, string? note, string blocker, DateTimeOffset? eligibleFrom)
    {
        Status = ErasureRequestStatus.Restricted;
        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionNote = Clean(note);
        Blocker = blocker;
        EligibleFrom = eligibleFrom;
    }

    /// <summary>The daily job's re-evaluation of a restricted request: the reason it still waits.</summary>
    public void StillBlocked(string blocker, DateTimeOffset? eligibleFrom)
    {
        Blocker = blocker;
        EligibleFrom = eligibleFrom;
    }

    public void Reject(Guid decidedBy, DateTimeOffset now, string note)
    {
        Status = ErasureRequestStatus.Rejected;
        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionNote = Clean(note);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
