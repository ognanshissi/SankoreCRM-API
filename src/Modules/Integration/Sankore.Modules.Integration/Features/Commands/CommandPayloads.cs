namespace Sankore.Modules.Integration.Features.Commands;

using Sankore.Modules.Integration.PublicApi;

// The payload a command carries, one record per operation whose arguments do not already fit in
// the row's own columns. Encrypted through CommandPayloadProtector and read back by the
// dispatcher, which is the only consumer: a payload never reaches a GET, an event or an audit
// value.
//
// Records and not a loose dictionary, for the reason the CommandType enum is an enum: the
// dispatcher's deserialisation then fails loudly on a payload that no longer matches the
// operation, instead of handing the adapter a null product code it will refuse anyway.
//
// A command whose arguments ARE its columns carries no payload at all:
// CreateCustomer carries the assembled CbsCustomerPayload, UpdateCustomer the same, and
// CancelPolicy only a reason.

/// <summary>The tier to set at the external system. Mapped from M02's own tier.</summary>
internal sealed record SetKycLevelPayload(KycLevel KycLevel);

/// <summary>
/// The product to open. A CRM product code, translated by the adapter through
/// <c>integration_mapping</c> — never passed through as-is.
/// </summary>
internal sealed record OpenAccountPayload(string ProductCode);

/// <summary>A debit of a premium against an account (ASS-05).</summary>
internal sealed record DebitAccountPayload(
    string AccountRef, decimal Amount, string Currency, string Label);

/// <summary>
/// The reversal of a debit, keyed on the ORIGINAL reference — the only form a core banking
/// system will safely accept twice.
/// </summary>
internal sealed record ReverseDebitPayload(string AccountRef, string OriginalReference);

/// <summary>Why a policy is being cancelled. Operator-facing, never personal data.</summary>
internal sealed record CancelPolicyPayload(string ExternalPolicyId, string Reason);

/// <summary>
/// A loan application. Kept as its own payload rather than reusing the PublicApi record because
/// the external customer id it needs is only known at send time, from the reference table.
/// </summary>
internal sealed record SubmitLoanApplicationPayload(
    string ProductCode, decimal Amount, string Currency, int TermMonths, string? Purpose);
