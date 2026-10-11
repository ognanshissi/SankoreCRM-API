namespace Sankore.Modules.Leads.Features.Household;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// What the lead's product line permits of its household (BNF-B-03, BNF-B-06).
/// </summary>
/// <param name="MaxChildren">Null = no ceiling.</param>
/// <param name="ChildMaxAge">Null = no limit.</param>
internal sealed record HouseholdLimits(int? MaxChildren, int? ChildMaxAge)
{
    /// <summary>What a lead with no resolved product line gets: allowed, with no limits.</summary>
    internal static HouseholdLimits Unlimited { get; } = new(null, null);
}

/// <summary>
/// Resolves the household rules of a lead's product line — the ceilings it configures, and nothing
/// that refuses a household outright.
/// </summary>
internal sealed class HouseholdPolicyResolver(IAdministrationModule administration)
{
    internal const string PolicyUnavailable = "PRODUCT_POLICY_UNAVAILABLE";

    /// <summary>
    /// The limits to apply.
    /// </summary>
    /// <remarks>
    /// <b>An UNRESOLVED product line is allowed, not refused.</b> An agent collects the household
    /// while the prospect is in front of them and qualifies the request afterwards; refusing until
    /// somebody picks a product line would invert that order for no benefit. The gap is reported
    /// by the completeness check instead, as <c>PRODUCT_LINE_NOT_RESOLVED</c> (BNF-B-04), and the
    /// conversion refuses on it.
    ///
    /// <para>
    /// <b>An M12 outage FAILS the write</b>, unlike the best-effort lookups on the capture path.
    /// The asymmetry is deliberate: a capture must never lose a prospect who is on the phone,
    /// whereas a household edit is a deliberate action the agent can repeat — and letting it
    /// through would silently bypass the child ceiling and the age limit that an institution
    /// configured.
    /// </para>
    /// </remarks>
    internal async Task<Result<HouseholdLimits>> ResolveAsync(Lead lead, CancellationToken ct)
    {
        if (lead.ProductLineId is not { } lineId)
            return Result.Ok(HouseholdLimits.Unlimited);

        BeneficiaryPolicyDto? policy;
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            policy = await administration.GetBeneficiaryPolicyAsync(lead.TenantId, lineId, ct);
            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<HouseholdLimits>(PolicyUnavailable);
        }

        // A DANGLING line id — the product was deleted, or the id was never valid. Treated like an
        // unresolved line rather than as a refusal: BNF-B-01 makes the reference opaque precisely
        // so a retired product degrades gracefully, and refusing the household would strand a
        // lead whose product line somebody else removed.
        if (policy is null)
            return Result.Ok(HouseholdLimits.Unlimited);

        // `policy.HouseholdEnabled` is deliberately NOT read. A household is a fact about the
        // prospect, not a feature of the product they are asking about: the agent has the family in
        // front of them and records it once, whatever line the request is later qualified as. The
        // flag used to refuse the write with HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE, which made the
        // household unrecordable on most lines — `BeneficiaryPolicy.HouseholdEnabled` defaults to
        // FALSE, so every line whose policy was saved without ticking it refused a spouse that
        // nobody had meant to forbid.
        //
        // The CEILINGS below still apply, because they bound a household this product will carry
        // rather than deciding whether one exists.
        return Result.Ok(new HouseholdLimits(policy.MaxChildren, policy.ChildMaxAge));
    }
}
