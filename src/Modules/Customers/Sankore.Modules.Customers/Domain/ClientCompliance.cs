namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>How a client pays and is paid (insurance lot 4).</summary>
public enum PaymentInstrumentKind
{
    MobileMoney,
    BankAccount,
}

/// <summary>
/// A client's Mobile Money number or bank account (RIB) — what a premium is collected from and a
/// refund paid to, and what a distance sale must record. The number is encrypted like every other
/// identifier of the client; screens see its last digits only.
/// </summary>
public sealed class ClientPaymentInstrument : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public PaymentInstrumentKind Kind { get; private set; }

    /// <summary>The Mobile Money operator or the bank.</summary>
    public string Provider { get; private set; } = default!;

    public string EncryptedIdentifier { get; private set; } = default!;

    /// <summary>The last characters, shown in place of the number.</summary>
    public string MaskedIdentifier { get; private set; } = default!;

    public string? HolderName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }

    public bool IsActive => DeactivatedAt is null;

    private ClientPaymentInstrument() { }

    public static ClientPaymentInstrument Register(
        Guid tenantId, Guid clientId, PaymentInstrumentKind kind, string provider, string clearIdentifier,
        string encryptedIdentifier, string? holderName, Guid by, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new DomainException("PAYMENT_INSTRUMENT_PROVIDER_REQUIRED", "PaymentInstrument.Provider.Required");

        var compact = new string(clearIdentifier.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length < 6)
            throw new DomainException("PAYMENT_INSTRUMENT_IDENTIFIER_INVALID", "PaymentInstrument.Identifier.Invalid");

        return new ClientPaymentInstrument
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Kind = kind,
            Provider = provider.Trim(),
            EncryptedIdentifier = encryptedIdentifier,
            MaskedIdentifier = "•••• " + compact[^4..],
            HolderName = string.IsNullOrWhiteSpace(holderName) ? null : holderName.Trim(),
            CreatedAt = now,
            CreatedBy = by,
        };
    }

    public void Deactivate(DateTimeOffset now) => DeactivatedAt ??= now;
}

public enum SuspicionAlertStatus
{
    /// <summary>Filed, not yet looked at by the compliance officer.</summary>
    Open,

    UnderReview,

    /// <summary>Declared to the CENTIF; the declaration's date and reference are recorded.</summary>
    Declared,

    /// <summary>Examined and closed without a declaration, with the reason.</summary>
    ClosedWithoutDeclaration,
}

/// <summary>
/// A confidential LBC/FT suspicion about a client (insurance lot 4, product owner 2026-10-25). Any
/// agent may file one; from then on only the compliance officer reads it. Nothing of it appears on
/// the client's record, timeline or exports, nothing is sent to the client, and it blocks nothing by
/// itself: the officer decides, and the declaration to the CENTIF is made outside the platform.
/// </summary>
public sealed class SuspicionAlert : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }

    /// <summary>What the agent observed, encrypted.</summary>
    public string EncryptedFacts { get; private set; } = default!;

    public Guid RaisedBy { get; private set; }
    public DateTimeOffset RaisedAt { get; private set; }

    public SuspicionAlertStatus Status { get; private set; }
    public Guid? HandledBy { get; private set; }
    public DateTimeOffset? HandledAt { get; private set; }

    public DateOnly? DeclaredOn { get; private set; }
    public string? DeclarationReference { get; private set; }

    /// <summary>The compliance officer's notes, encrypted.</summary>
    public string? EncryptedNotes { get; private set; }

    private SuspicionAlert() { }

    public static SuspicionAlert Raise(Guid tenantId, Guid clientId, string encryptedFacts, Guid by, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            EncryptedFacts = encryptedFacts,
            RaisedBy = by,
            RaisedAt = now,
            Status = SuspicionAlertStatus.Open,
        };

    public Result Handle(
        SuspicionAlertStatus status, DateOnly? declaredOn, string? declarationReference, string? encryptedNotes,
        Guid by, DateTimeOffset now)
    {
        if (Status is SuspicionAlertStatus.Declared or SuspicionAlertStatus.ClosedWithoutDeclaration)
            return Result.Fail("SUSPICION_ALERT_CLOSED");
        if (status == SuspicionAlertStatus.Open)
            return Result.Fail("SUSPICION_ALERT_INVALID_STATUS");
        if (status == SuspicionAlertStatus.Declared && (declaredOn is null || string.IsNullOrWhiteSpace(declarationReference)))
            return Result.Fail("SUSPICION_ALERT_DECLARATION_REQUIRED");
        if (status == SuspicionAlertStatus.ClosedWithoutDeclaration && string.IsNullOrWhiteSpace(encryptedNotes))
            return Result.Fail("SUSPICION_ALERT_REASON_REQUIRED");

        Status = status;
        HandledBy = by;
        HandledAt = now;
        if (status == SuspicionAlertStatus.Declared)
        {
            DeclaredOn = declaredOn;
            DeclarationReference = declarationReference!.Trim();
        }
        if (!string.IsNullOrWhiteSpace(encryptedNotes)) EncryptedNotes = encryptedNotes;
        return Result.Ok();
    }
}
