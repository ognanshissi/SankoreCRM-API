namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>Where a WhatsApp complaint intake has got to: the question the customer is answering.</summary>
public enum WhatsAppComplaintStep
{
    ClientNumber,
    BirthDate,
    Description,
    Category,
    Confirmation,
    Ended,
}

public enum WhatsAppComplaintOutcome
{
    /// <summary>Recorded in the client's register (M01).</summary>
    Filed,

    /// <summary>The customer said no, or « annuler ».</summary>
    Cancelled,

    /// <summary>Handed to a person: identification failed, a person was asked for, the register refused.</summary>
    Escalated,

    /// <summary>The customer stopped answering; the next message is an ordinary one again.</summary>
    Expired,
}

/// <summary>
/// A complaint being taken on WhatsApp, one question at a time, with no model involved (product
/// owner, 2026-10-26): the customer is identified by client number and date of birth, describes
/// the complaint, picks its category, confirms — then it is recorded on their client record.
///
/// <para>
/// <b>A row of its own and not columns on the conversation</b>: a conversation can see several
/// intakes over its life, and how many failed identifications a number made in the last day is a
/// count over these rows — the bound that stops somebody restarting the intake to buy fresh tries.
/// </para>
///
/// <para>
/// What the customer typed waits here encrypted (M13's key) between two messages — the client
/// number until the date arrives, the description until the confirmation. Both are cleared when
/// the intake ends: once the complaint is in M01's register, this module has no reason to hold it.
/// </para>
/// </summary>
public sealed class WhatsAppComplaintIntake : ITenant
{
    /// <summary>Failed identifications allowed per conversation over <see cref="AttemptWindow"/>.</summary>
    public const int MaxFailedIdentifications = 3;

    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);

    /// <summary>Silence after which an intake no longer captures the customer's messages.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public const int DescriptionMaxLength = 2000;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public WhatsAppComplaintStep Step { get; private set; }
    public int FailedIdentifications { get; private set; }

    /// <summary>Plain here, ciphertext in the column. Held only between the number and the date.</summary>
    public string? PendingClientNumber { get; private set; }

    /// <summary>The client the number and the date designated. Opaque reference into M01.</summary>
    public Guid? ClientId { get; private set; }

    /// <summary>Plain here, ciphertext in the column. Held only until the confirmation.</summary>
    public string? Description { get; private set; }

    /// <summary>An M01 category name (Credit, Savings…).</summary>
    public string? Category { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public WhatsAppComplaintOutcome? Outcome { get; private set; }

    /// <summary>Why it was handed to a person, for the agent and the metrics. A code, never a value.</summary>
    public string? EndReason { get; private set; }

    public Guid? ComplaintId { get; private set; }
    public string? ComplaintReference { get; private set; }

    private WhatsAppComplaintIntake() { }

    public static WhatsAppComplaintIntake Start(Guid tenantId, Guid conversationId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (conversationId == Guid.Empty) throw new DomainException("An intake runs in a conversation.");

        return new WhatsAppComplaintIntake
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Step = WhatsAppComplaintStep.ClientNumber,
            StartedAt = now,
            LastActivityAt = now,
        };
    }

    public bool IsActiveAt(DateTimeOffset now) => Step != WhatsAppComplaintStep.Ended && now - LastActivityAt <= IdleTimeout;

    public void Touch(DateTimeOffset now) => LastActivityAt = now;

    public void RecordClientNumber(string clientNumber)
    {
        PendingClientNumber = clientNumber.Trim();
        Step = WhatsAppComplaintStep.BirthDate;
    }

    public void RecordIdentified(Guid clientId)
    {
        ClientId = clientId;
        PendingClientNumber = null;
        Step = WhatsAppComplaintStep.Description;
    }

    /// <summary>Back to the number: the customer may have mistyped either, and is not told which.</summary>
    public void RecordIdentificationFailed()
    {
        FailedIdentifications++;
        PendingClientNumber = null;
        Step = WhatsAppComplaintStep.ClientNumber;
    }

    public void RecordDescription(string description)
    {
        var trimmed = description.Trim();
        Description = trimmed.Length <= DescriptionMaxLength ? trimmed : trimmed[..DescriptionMaxLength];
        Step = WhatsAppComplaintStep.Category;
    }

    public void RecordCategory(string category)
    {
        Category = category;
        Step = WhatsAppComplaintStep.Confirmation;
    }

    public void RecordFiled(Guid complaintId, string reference, DateTimeOffset now)
    {
        ComplaintId = complaintId;
        ComplaintReference = reference;
        End(WhatsAppComplaintOutcome.Filed, reason: null, now);
    }

    /// <summary>The person is erased with their conversation: what they typed goes, the count of tries stays.</summary>
    public void Anonymize(DateTimeOffset now) => End(WhatsAppComplaintOutcome.Expired, "ANONYMIZED", now);

    public void End(WhatsAppComplaintOutcome outcome, string? reason, DateTimeOffset now)
    {
        if (Step == WhatsAppComplaintStep.Ended) return;

        Step = WhatsAppComplaintStep.Ended;
        Outcome = outcome;
        EndReason = reason;
        EndedAt = now;
        PendingClientNumber = null;
        Description = null;
    }
}
