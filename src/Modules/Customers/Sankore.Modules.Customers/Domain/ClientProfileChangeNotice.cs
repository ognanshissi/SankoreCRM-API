namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The institution LEARNED that something about a client has changed, and owes an update to its
/// information system — the CIMA LBC/FT obligation that « toute information modifiant le profil du
/// client doit être intégrée au système d'information dans un délai maximum de 15 jours ».
///
/// <para>
/// <b>Why a row at all.</b> The deadline is measurable only against the moment the institution
/// found out, and nothing recorded that. <c>Client.UpdatedAt</c> says when the file was changed,
/// which is the END of the fifteen days; the beginning is an agent being told on the telephone
/// that a client has moved. Without this row an institution can be in breach for a month and have
/// no way to know, and a supervisor asking "how long do your profile updates take" has no answer.
/// </para>
///
/// <para>
/// <b>It records the KIND of change, not the new value.</b> The value belongs in the client
/// record, where it is encrypted and reached through an audited path; a notice saying "moved to 12
/// rue des Jardins" would be an unencrypted address in a second table and a second place to purge.
/// <see cref="Note"/> is for an operational remark — "spouse called, will bring the lease" — and
/// the type's own documentation says so, because the field is the obvious place to paste the
/// address.
/// </para>
///
/// <para>
/// <b>A report, not a block.</b> Nothing refuses a write because a notice is overdue: the breach
/// has already happened by then, and refusing the very update that would close it would be
/// perverse. <c>GET clients/profile-change-notices/overdue</c> is the answer.
/// </para>
/// </summary>
public sealed class ClientProfileChangeNotice : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }

    public ProfileChangeKind Kind { get; private set; }

    /// <summary>
    /// When the INSTITUTION learned of the change — not when the change happened, and not when
    /// the file was updated. It is the start of the fifteen days.
    ///
    /// <para>
    /// Caller-supplied, because an agent records on Monday what a client told them on Friday. The
    /// future is refused (a change nobody has heard of yet cannot start a clock) and so is a date
    /// before the client existed.
    /// </para>
    /// </summary>
    public DateOnly NoticedOn { get; private set; }

    /// <summary>
    /// The authenticated user who recorded the notice — never a caller-supplied id, the rule this
    /// solution applies to every <c>RecordedBy</c>.
    /// </summary>
    public Guid RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// An operational remark. <b>Must not carry the new value</b> — see the type's own remarks.
    /// </summary>
    public string? Note { get; private set; }

    /// <summary>When the change was actually carried into the client record. Null while open.</summary>
    public DateOnly? IntegratedOn { get; private set; }

    public Guid? IntegratedBy { get; private set; }

    /// <summary>
    /// Why a notice was closed WITHOUT an update — the client was mistaken, the change turned out
    /// to be about somebody else. Required in that case: a notice that simply disappears is
    /// indistinguishable from one somebody forgot.
    /// </summary>
    public string? DismissedReason { get; private set; }

    public bool IsOpen => IntegratedOn is null && DismissedReason is null;

    private ClientProfileChangeNotice() { } // EF Core

    public static ClientProfileChangeNotice Record(
        Guid tenantId,
        Guid clientId,
        ProfileChangeKind kind,
        DateOnly noticedOn,
        Guid recordedBy,
        DateTimeOffset now,
        string? note = null)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.", "ProfileNotice.Tenant.Required");
        if (clientId == Guid.Empty)
            throw new DomainException("ClientId is required.", "ProfileNotice.Client.Required");

        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (noticedOn > today)
        {
            throw new DomainException(
                "A change cannot have been noticed in the future.", "ProfileNotice.Date.Future");
        }

        return new ClientProfileChangeNotice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Kind = kind,
            NoticedOn = noticedOn,
            RecordedBy = recordedBy,
            RecordedAt = now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
    }

    /// <summary>The change has been carried into the client record. Closes the clock.</summary>
    public Result Integrate(Guid actor, DateOnly on)
    {
        if (!IsOpen) return Result.Fail(CustomerErrors.ProfileNoticeAlreadyClosed);

        if (on < NoticedOn)
        {
            // Integrated before it was noticed is a data-entry error whose effect would be a
            // NEGATIVE delay in the compliance report — a figure nobody can act on.
            return Result.Fail(CustomerErrors.ProfileNoticeIntegratedBeforeNoticed);
        }

        IntegratedOn = on;
        IntegratedBy = actor;
        return Result.Ok();
    }

    /// <summary>The notice turned out to need no update. Closes it WITH a reason.</summary>
    public Result Dismiss(string reason, Guid actor, DateOnly on)
    {
        if (!IsOpen) return Result.Fail(CustomerErrors.ProfileNoticeAlreadyClosed);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ProfileNoticeDismissReasonRequired);

        DismissedReason = reason.Trim();
        IntegratedBy = actor;
        IntegratedOn = null;

        // Recorded on the same field the integration uses so the row always says who closed it,
        // whichever way it was closed.
        _ = on;
        return Result.Ok();
    }

    /// <summary>
    /// Days taken to integrate, or days elapsed so far for an open notice.
    /// <paramref name="asOf"/> is explicit so the report and a screen agree.
    /// </summary>
    public int ElapsedDays(DateOnly asOf)
        => (IntegratedOn ?? asOf).DayNumber - NoticedOn.DayNumber;

    /// <summary>
    /// Whether the fifteen-day obligation is breached <paramref name="asOf"/>.
    ///
    /// <para>
    /// The single definition, so the report and any screen cannot disagree. A DISMISSED notice is
    /// never overdue — there was nothing to integrate — while an open one goes on accruing.
    /// </para>
    /// </summary>
    public bool IsOverdue(DateOnly asOf, int allowedDays)
        => DismissedReason is null && ElapsedDays(asOf) > allowedDays;
}

/// <summary>
/// What changed about the client. A closed list, because the point of the record is to be
/// reportable: "how many address changes took more than fifteen days" is the question, and free
/// text answers none of it.
/// </summary>
public enum ProfileChangeKind
{
    Address,
    Phone,
    Email,
    Name,
    MaritalStatus,
    Profession,
    ActivitySector,
    IdentityDocument,
    DeclaredIncome,

    /// <summary>A change of beneficial owner or of a legal entity's officers.</summary>
    Control,

    /// <summary>The client became, or stopped being, politically exposed.</summary>
    PepStatus,

    Other,
}
