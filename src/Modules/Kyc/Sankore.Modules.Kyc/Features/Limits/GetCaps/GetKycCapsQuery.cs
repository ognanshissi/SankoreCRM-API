namespace Sankore.Modules.Kyc.Features.Limits.GetCaps;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The ceilings in force for one customer, for the screens that must respect them (KYC-B-06).
///
/// <para>
/// A query — no <c>ICommand</c>, so neither the transaction nor the audit behaviour runs. It is the
/// read side of <c>IKycModule.GetLimitsAsync</c>, which until now was reachable only from another
/// module's C# code: the teller screen, the customer file and the credit screen had no way to learn
/// the amounts and the front was left hard-coding them.
/// </para>
/// </summary>
internal sealed record GetKycCapsQuery(Guid CustomerId) : IRequest<Result<KycCapsDto>>;

/// <summary>
/// What a screen needs to respect the simplified-tier ceilings — and nothing it could mistake for
/// a measurement it does not have.
/// </summary>
/// <param name="IsCapped">
/// False for a full KYC. Every ceiling below is then <c>null</c>, deliberately: an uncapped
/// customer has no ceiling, and sending a number a screen could draw a gauge against would invent
/// a limit that does not exist.
/// </param>
/// <param name="Currency">
/// ISO 4217 code the ceilings are expressed in, from the tenant parameter
/// <c>caps-currency</c>. The amounts are bare decimals in the database, so the currency has to
/// travel with them — a screen that assumes XOF is a screen that misreports a tenant outside the
/// franc zone.
/// </param>
/// <param name="FlowWindowDays">
/// Width of the ROLLING window the flow ceiling applies to — 30 days by factory default, and
/// configurable per tenant. It is not a calendar month: a label saying "ce mois-ci" is wrong for
/// any tenant that changed it, and wrong on the 1st of the month for every tenant.
/// </param>
/// <param name="AlertPct">Share of the flow ceiling at which the agent is warned, in percent.</param>
public sealed record KycCapsDto(
    Guid CustomerId,
    Guid KycFileId,
    string Tier,
    bool IsCapped,
    string Currency,
    decimal? BalanceCap,
    decimal? FlowCap,
    int? FlowWindowDays,
    int? AlertPct,
    KycCapsUsageDto Usage);

/// <summary>
/// What the customer has consumed of those ceilings — or, today, the explicit statement that
/// nobody knows.
/// </summary>
/// <param name="Balance">
/// <c>null</c> means NOT MEASURED, never zero. No module in this solution owns an account or a
/// balance: M03 (Épargne) and M07 (Mobile Money) do not exist. A caller must read a null as "I
/// cannot tell whether this operation fits" and refuse, exactly as
/// <c>IKycModule.GetFlowUsageAsync</c> forces its own callers to.
/// </param>
/// <param name="Flow">
/// Deposits plus withdrawals over the rolling window. Same rule: <c>null</c> is unmeasured, not
/// zero. It comes from <c>IKycModule.GetFlowUsageAsync</c>, so the day a transaction-owning module
/// lands, this field fills itself in without a change here.
/// </param>
/// <param name="FlowWindowStart">Start of the window <paramref name="Flow"/> was summed over.</param>
/// <param name="UnavailableReason">
/// Set whenever either amount is unmeasured, so a screen can SAY why instead of showing an empty
/// gauge. A stable code, translated by the front like every other error code of this module.
/// </param>
public sealed record KycCapsUsageDto(
    decimal? Balance,
    decimal? Flow,
    DateTimeOffset? FlowWindowStart,
    string? UnavailableReason);

/// <summary>Reasons a consumption figure is absent. Codes, not sentences.</summary>
public static class KycCapsUsageReasons
{
    /// <summary>
    /// No module owns accounts, balances or transactions yet, so the consumption cannot be summed
    /// at all. This is the only reason today, and it will stop being returned for the flow the day
    /// M03/M07 implement the measurement behind <c>IKycModule.GetFlowUsageAsync</c>.
    /// </summary>
    public const string NoTransactionSource = "KYC_USAGE_NO_TRANSACTION_SOURCE";
}
