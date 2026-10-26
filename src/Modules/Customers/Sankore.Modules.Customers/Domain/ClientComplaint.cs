namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>How the complaint reached the institution.</summary>
public enum ClientComplaintChannel
{
    /// <summary>At a counter, taken down by an agent.</summary>
    Branch,
    Telephone,
    Email,

    /// <summary>Through the WhatsApp bot, with no agent behind it.</summary>
    WhatsApp,
    Post,
}

/// <summary>
/// What the complaint is about. A closed list rather than free text, so the register can say how
/// many complaints concern credit, fees or the welcome at the counter — a question a subject line
/// cannot answer.
/// </summary>
public enum ClientComplaintCategory
{
    /// <summary>Anything the list does not name.</summary>
    Other,
    Credit,
    Savings,
    FeesAndPricing,
    PaymentMeans,

    /// <summary>The welcome, a delay, an agent's behaviour.</summary>
    Service,
    DataProtection,
}

public enum ClientComplaintStatus
{
    /// <summary>Recorded. The acknowledgement is owed.</summary>
    Received,

    /// <summary>The client has been told it is in hand. The answer is owed.</summary>
    Acknowledged,

    /// <summary>Answered, whatever the answer was. Terminal.</summary>
    Resolved,

    /// <summary>
    /// Withdrawn by the client, or outside the register's scope. Terminal, and distinct from
    /// <see cref="Resolved"/>: counting a withdrawal as an answer would overstate how many
    /// complaints the institution actually treated.
    /// </summary>
    Closed,
}

/// <summary>Which deadline a reminder is about.</summary>
public enum ClientComplaintDeadline
{
    Acknowledgement,
    Resolution,
}

/// <summary>
/// One client complaint, whatever the product (product owner, 2026-10-26).
///
/// <para>
/// <b>Not the insurance register.</b> <c>InsuranceComplaint</c> (Integration) serves the règlement
/// 01/CIMA/2024 and its own deadlines; it refuses credit and savings grievances on purpose. This
/// one is the institution's general register, on the client record, and an insurance complaint
/// still goes to the CIMA one.
/// </para>
///
/// <para>
/// <b>The deadlines are stored, not computed</b>, because the periods are tenant settings: a
/// deadline recomputed from today's setting would move under a complaint already late.
/// </para>
///
/// <para>
/// <b>The step history is the audit trail</b> — every write goes through a MediatR command, and
/// <c>AuditBehavior</c> records actor and instant. This row keeps only the instants a deadline or
/// a report is computed from.
/// </para>
/// </summary>
public sealed class ClientComplaint : ITenant
{
    public const int SubjectMaxLength = 200;
    public const int ReasonMaxLength = 500;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// <c>RCL-2026-000042</c>, per tenant and per year — what the client is given and quotes back.
    /// RCL and not REC: REC is the insurance register's prefix, and two « REC-2026-000042 » in one
    /// institution would be two different complaints under one number.
    /// </summary>
    public string Reference { get; private set; } = default!;

    public Guid ClientId { get; private set; }

    /// <summary>The client's agency when it was recorded — the perimeter and the manager to chase.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The contract concerned, when there is one. Opaque, no foreign key.</summary>
    public Guid? HoldingId { get; private set; }

    public ClientComplaintChannel Channel { get; private set; }
    public ClientComplaintCategory Category { get; private set; }

    /// <summary>One line, in clear: it appears in lists and notifications.</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>
    /// What the client actually said, encrypted with the module's field key. A grievance is free
    /// text about a named person's money, written by somebody who is angry. Nothing searches it.
    /// </summary>
    public string? BodyEncrypted { get; private set; }

    public ClientComplaintStatus Status { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Who recorded it. <see cref="Guid.Empty"/> = SYSTEM (the WhatsApp bot).</summary>
    public Guid ReceivedBy { get; private set; }

    /// <summary>The WhatsApp conversation it came from. Opaque reference into M13.</summary>
    public Guid? SourceConversationId { get; private set; }

    /// <summary>Who must handle it: the advisor, else the creator, else the agency manager. Null = nobody found.</summary>
    public Guid? AssignedTo { get; private set; }

    public DateTimeOffset AcknowledgementDueAt { get; private set; }
    public DateTimeOffset ResolutionDueAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public Guid? AcknowledgedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }

    /// <summary>The answer given to the client, encrypted like the complaint itself.</summary>
    public string? ResponseEncrypted { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public string? ClosureReason { get; private set; }

    /// <summary>When the manager was told the acknowledgement is late — so a daily sweep tells them once.</summary>
    public DateTimeOffset? AcknowledgementOverdueNotifiedAt { get; private set; }

    /// <summary>Same, for the answer.</summary>
    public DateTimeOffset? ResolutionOverdueNotifiedAt { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

    private ClientComplaint() { }

    public static ClientComplaint Record(
        Guid tenantId,
        string reference,
        Guid clientId,
        Guid agencyId,
        Guid? holdingId,
        ClientComplaintChannel channel,
        ClientComplaintCategory category,
        string subject,
        string? bodyEncrypted,
        Guid receivedBy,
        Guid? sourceConversationId,
        Guid? assignedTo,
        DateTimeOffset receivedAt,
        int acknowledgementDays,
        int resolutionDays)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (clientId == Guid.Empty) throw new DomainException("A complaint answers a client.");
        if (string.IsNullOrWhiteSpace(reference)) throw new DomainException("A complaint needs a reference.");
        if (string.IsNullOrWhiteSpace(subject)) throw new DomainException("A complaint needs a subject.");
        if (acknowledgementDays < 0 || resolutionDays < acknowledgementDays)
            throw new DomainException("The answer cannot be due before the acknowledgement.");

        return new ClientComplaint
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Reference = reference,
            ClientId = clientId,
            AgencyId = agencyId,
            HoldingId = holdingId,
            Channel = channel,
            Category = category,
            Subject = Clip(subject, SubjectMaxLength),
            BodyEncrypted = bodyEncrypted,
            Status = ClientComplaintStatus.Received,
            ReceivedAt = receivedAt,
            ReceivedBy = receivedBy,
            SourceConversationId = sourceConversationId,
            AssignedTo = assignedTo,
            AcknowledgementDueAt = receivedAt.AddDays(acknowledgementDays),
            ResolutionDueAt = receivedAt.AddDays(resolutionDays),
        };
    }

    public bool IsOpen => Status is ClientComplaintStatus.Received or ClientComplaintStatus.Acknowledged;

    public Result Acknowledge(Guid actorId, DateTimeOffset now)
    {
        if (Status != ClientComplaintStatus.Received) return Result.Fail(CustomerErrors.ComplaintInvalidTransition);

        Status = ClientComplaintStatus.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = actorId;
        return Result.Ok();
    }

    /// <summary>
    /// An answer given straight away also acknowledges: the client has been told it is in hand by
    /// being told how it ends, and the acknowledgement deadline must not read as breached.
    /// </summary>
    public Result Resolve(Guid actorId, string? responseEncrypted, DateTimeOffset now)
    {
        if (!IsOpen) return Result.Fail(CustomerErrors.ComplaintInvalidTransition);
        if (string.IsNullOrWhiteSpace(responseEncrypted)) return Result.Fail(CustomerErrors.ComplaintResponseRequired);

        if (AcknowledgedAt is null)
        {
            AcknowledgedAt = now;
            AcknowledgedBy = actorId;
        }

        Status = ClientComplaintStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = actorId;
        ResponseEncrypted = responseEncrypted;
        return Result.Ok();
    }

    public Result Close(Guid actorId, string reason, DateTimeOffset now)
    {
        if (!IsOpen) return Result.Fail(CustomerErrors.ComplaintInvalidTransition);
        if (string.IsNullOrWhiteSpace(reason)) return Result.Fail(CustomerErrors.ReasonRequired);

        Status = ClientComplaintStatus.Closed;
        ClosedAt = now;
        ClosedBy = actorId;
        ClosureReason = Clip(reason, ReasonMaxLength);
        return Result.Ok();
    }

    /// <summary>The deadline that is late and nobody has been told about yet, if any — the answer's first.</summary>
    public ClientComplaintDeadline? OverdueToReport(DateTimeOffset now)
    {
        if (!IsOpen) return null;
        if (now > ResolutionDueAt && ResolutionOverdueNotifiedAt is null) return ClientComplaintDeadline.Resolution;
        if (AcknowledgedAt is null && now > AcknowledgementDueAt && AcknowledgementOverdueNotifiedAt is null)
            return ClientComplaintDeadline.Acknowledgement;
        return null;
    }

    public void MarkOverdueReported(ClientComplaintDeadline deadline, DateTimeOffset now)
    {
        if (deadline == ClientComplaintDeadline.Resolution)
        {
            ResolutionOverdueNotifiedAt = now;
            // A late answer implies a late acknowledgement already reported or moot: one reminder.
            AcknowledgementOverdueNotifiedAt ??= now;
        }
        else
        {
            AcknowledgementOverdueNotifiedAt = now;
        }
    }

    /// <summary>
    /// The client was erased: what they said and what they were told goes, the row stays so the
    /// register still counts it (category, channel, dates). The subject goes too — an agent may
    /// have typed a name in it.
    /// </summary>
    public void Anonymize(DateTimeOffset now)
    {
        if (AnonymizedAt is not null) return;

        Subject = "Réclamation anonymisée";
        BodyEncrypted = null;
        ResponseEncrypted = null;
        ClosureReason = ClosureReason is null ? null : "Anonymisé";
        AnonymizedAt = now;
    }

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
