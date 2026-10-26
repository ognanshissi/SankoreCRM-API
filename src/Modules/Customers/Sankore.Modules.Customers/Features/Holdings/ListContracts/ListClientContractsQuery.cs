namespace Sankore.Modules.Customers.Features.Holdings.ListContracts;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// Everything a client holds, as ONE list: the contracts captured here, plus the policies and the
/// accounts and loans the back offices hold that no contract here accounts for (product owner,
/// 2026-10-26 — the client record answers « what does this client hold », not « which system says
/// so »).
/// </summary>
public sealed record ListClientContractsQuery(Guid ClientId)
    : IClientReadRequest, IRequest<Result<ClientContractsDto>>;

/// <summary>
/// One line of the client's contract list.
/// </summary>
/// <param name="Key">
/// Stable and unique within the list: <c>holding:{id}</c>, <c>policy:{id}</c>,
/// <c>account:{ref}</c> or <c>loan:{ref}</c>. A line is a holding whenever one exists — the
/// policy or account that realises it is folded into it, never listed a second time.
/// </param>
/// <param name="Source">
/// <c>Crm</c> (a contract captured here), <c>Insurer</c> or <c>CoreBanking</c> (held only by
/// that back office — read-only here).
/// </param>
/// <param name="Nature"><c>Insurance</c>, <c>Account</c>, <c>Loan</c> or <c>Other</c>.</param>
/// <param name="Status">
/// The holding's status for a <c>Crm</c> line, the back office's own word otherwise.
/// </param>
/// <param name="ExternalStatus">
/// For a <c>Crm</c> line, the status of the policy or account that realises it, when there is one.
/// </param>
/// <param name="DesignationsToReview">
/// Current designations of a <c>Crm</c> line flagged « à revoir » (a household change touched the
/// person named): the agent confirms role and share from the contract.
/// </param>
/// <param name="AmountKind">
/// What <paramref name="Amount"/> is: <c>Premium</c>, <c>Balance</c>, <c>Outstanding</c>,
/// <c>InsuredCapital</c> or <c>Deposit</c>. Two kinds are never added together — a screen shows
/// each with its own word.
/// </param>
public sealed record ClientContractDto(
    string Key,
    string Source,
    string Nature,
    string Label,
    string? ProductCode,
    string? Category,
    string Status,
    string? ExternalStatus,
    string? Reference,
    decimal? Amount,
    string? AmountKind,
    string? Currency,
    DateOnly? StartDate,
    Guid? HoldingId,
    Guid? PolicyId,
    string? AccountRef,
    string? LoanRef,
    string? Insurer,
    bool PremiumOutstanding,
    int DesignationsToReview = 0,
    /// <summary>A Crm line's signature date: « signé, non envoyé » is told apart by it.</summary>
    DateTimeOffset? SignedAt = null);

/// <param name="UnavailableSources">
/// <c>Insurer</c> and/or <c>CoreBanking</c> when that back office's records could not be read:
/// the list is then partial and the screen says so, rather than showing a client as holding less
/// than they do.
/// </param>
/// <param name="CoreBankingAsOf">When the core banking figures were true, if any were read.</param>
public sealed record ClientContractsDto(
    IReadOnlyList<ClientContractDto> Items,
    IReadOnlyList<string> UnavailableSources,
    DateTimeOffset? CoreBankingAsOf);
