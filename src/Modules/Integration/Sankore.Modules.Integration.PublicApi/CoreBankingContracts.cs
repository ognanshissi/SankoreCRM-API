namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What SANKORE knows about a customer, in the shape an adapter maps onto its own API.
///
/// <para>
/// Codes travel as CRM codes (<see cref="IdDocumentType"/>, <see cref="Gender"/>,
/// <see cref="AgencyCode"/>…) and are translated by the adapter through
/// <c>integration_mapping</c>. A missing translation is a <see cref="ErrorFamily.Technical"/>
/// failure naming the domain and the code — never a silent pass-through of a CRM code the
/// external system has never heard of (INT-04).
/// </para>
/// </summary>
public sealed record CbsCustomerPayload(
    Guid CrmCustomerId,
    string? FirstName,
    string? LastName,
    string? LegalName,
    DateOnly? DateOfBirth,
    string? Gender,
    string? MaritalStatus,
    string? Nationality,
    string? IdDocumentType,
    string? IdDocumentNumber,
    string? PhoneNumber,
    string? Email,
    string? AddressLine,
    string? City,
    string? Country,
    string? Profession,
    string? Sector,
    string? AgencyCode,
    KycLevel KycLevel,
    string? CrmReference);

public sealed record CbsAccount(
    ExternalId AccountId,
    string AccountNumber,
    string? ProductCode,
    string? ProductLabel,
    string Currency,
    decimal Balance,
    decimal? AvailableBalance,
    string Status,
    DateOnly? OpenedOn);

/// <summary>
/// A balance and the moment it was true. <paramref name="IsStale"/> says the figure comes from
/// the snapshot rather than from the CBS — the live call failed or the breaker is open — so the
/// counter clerk is told rather than shown a stale number as if it were current (INT-15).
/// </summary>
public sealed record CbsBalance(
    ExternalId AccountId,
    string Currency,
    decimal Balance,
    decimal? AvailableBalance,
    DateTimeOffset AsOf,
    bool IsStale = false);

public sealed record CbsTransaction(
    string Reference,
    DateOnly ValueDate,
    DateTimeOffset? BookedAt,
    decimal Amount,
    string Currency,
    string Direction,
    string? Label,
    string? CounterpartyLabel);

/// <summary>One page of transactions. <paramref name="NextCursor"/> null means the end.</summary>
public sealed record CbsPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Money in and out over one month, which is what the simplified-KYC flow ceiling is measured
/// against (INT-22).
/// </summary>
public sealed record CbsMonthlyFlow(
    YearMonth Month,
    decimal CreditTotal,
    decimal DebitTotal,
    string Currency)
{
    /// <summary>The figure the ceiling applies to: everything that moved.</summary>
    public decimal Total => CreditTotal + DebitTotal;
}

public sealed record CbsLoan(
    ExternalId LoanId,
    string? ProductCode,
    decimal PrincipalAmount,
    decimal OutstandingAmount,
    string Currency,
    string Status,
    DateOnly? DisbursedOn,
    DateOnly? MaturityOn,
    int? DaysInArrears);

public sealed record CbsLoanApplicationPayload(
    Guid CrmCustomerId,
    ExternalId CustomerId,
    string ProductCode,
    decimal Amount,
    string Currency,
    int TermMonths,
    string? Purpose);

/// <summary>
/// The consolidated read model Customer 360 displays without touching the CBS (INT-21).
/// <paramref name="SnapshotAt"/> is shown next to the figures: a read model that cannot say how
/// old it is cannot be trusted at a counter.
/// </summary>
public sealed record CbsCustomerSnapshot(
    Guid CrmCustomerId,
    IReadOnlyList<CbsAccount> Accounts,
    IReadOnlyList<CbsLoan> Loans,
    decimal TotalBalance,
    decimal MonthlyFlow,
    KycLevel? KycLevelInCbs,
    DateTimeOffset SnapshotAt);

/// <summary>Result of a premium debit, kept for the insurance statement (ASS-05, ASS-10).</summary>
public sealed record CbsDebitReceipt(
    ExternalId AccountId,
    string Reference,
    decimal Amount,
    string Currency,
    DateTimeOffset PostedAt);
