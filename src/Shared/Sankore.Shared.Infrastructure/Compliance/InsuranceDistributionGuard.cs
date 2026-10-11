namespace Sankore.Shared.Infrastructure.Compliance;

using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Compliance;

/// <summary>
/// The CIMA professional-card requirement, in one place: article 503 lets a microfinance
/// institution present insurance operations at its counters <b>on condition that the person
/// presenting them holds the card of article 510</b>.
///
/// <para>
/// Two call sites and one definition. The dispatching pool filters on it so an insurance lead is
/// not routed to an unlicensed agent, and the subscription path refuses on it so a contract does
/// not leave the institution under an unlicensed signature. Writing the rule twice is how the two
/// would come to disagree about an expired indemnity cover.
/// </para>
///
/// <para>
/// <b>The two postures are deliberately different</b>, and they follow the asymmetry this
/// repository already documents for cross-module failures:
/// </para>
/// <list type="table">
///   <item>
///     <term>Dispatching</term>
///     <description>
///     narrows the pool, and only for an INSURANCE product line. A lead on a savings line is
///     untouched, because the licence has nothing to do with it. On an M12 outage the pool is
///     left UNCHANGED — permissive, deliberately: emptying every insurance pool because a
///     credential lookup failed would park every lead on <c>NO_AGENT_AVAILABLE</c> while the SLA
///     clock runs, and no contract has left the institution yet. The subscription gate below is
///     what still refuses at the end of that road.
///     </description>
///   </item>
///   <item>
///     <term>Subscription</term>
///     <description>
///     fails the write, and fails closed on an outage. This is the last code that runs before a
///     contract leaves the institution, the same reason BNF-B-16 re-checks the client's signature
///     in the Integration consumer rather than trusting a second-hand word for it.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>What it deliberately does NOT do</b> is decide whether an institution records cards at all.
/// A tenant that has recorded none gets <see cref="DistributionEligibility.Missing"/> for every
/// user, which the subscription gate refuses. That is the regulation's answer, not a
/// configuration — but it is also why <see cref="CredentialRequired"/> is a named, stable error
/// code rather than a generic refusal: the first thing an institution hitting it needs is to be
/// told what to record.
/// </para>
/// </summary>
public sealed class InsuranceDistributionGuard(
    IDistributionComplianceProvider compliance,
    TimeProvider clock,
    ILogger<InsuranceDistributionGuard> logger)
{
    /// <summary>The holder of the signature or the assignment has no valid professional card.</summary>
    public const string CredentialRequired = "DISTRIBUTION_CREDENTIAL_REQUIRED";

    /// <summary>
    /// Whether <paramref name="productLineId"/> is an insurance line, and therefore whether a
    /// card is required at all.
    ///
    /// <para>
    /// A null line id means NOT required: a lead whose product has not been resolved yet is the
    /// ordinary state of a capture, and refusing to dispatch it would make the licence rule a
    /// conversion blocker. The subscription path never sees a null — a holding always carries its
    /// line.
    /// </para>
    ///
    /// <para>
    /// A DANGLING line id also means not required, consistent with the rest of this solution:
    /// <c>Lead.ProductLineId</c> is an opaque reference by construction, and a lead whose product
    /// somebody has since removed must not become unworkable.
    /// </para>
    /// </summary>
    public async Task<bool> RequiresCredentialAsync(
        Guid tenantId, Guid? productLineId, CancellationToken ct)
    {
        if (productLineId is not { } lineId) return false;

        // Through the Kernel contract, which M12 answers from the SAME cached catalogue read the
        // beneficiary policy uses. So the guard costs no extra query on the overwhelming majority
        // of dispatches, which are not insurance.
        return await compliance.IsInsuranceProductLineAsync(tenantId, lineId, ct);
    }

    /// <summary>
    /// Refuses when <paramref name="userId"/> may not present insurance operations for
    /// <paramref name="productLineId"/>. Success for a non-insurance line, whatever the user's
    /// credential says.
    ///
    /// <para>
    /// <b>Fails closed.</b> An exception from the catalogue or the credential lookup is a refusal,
    /// not a pass — see the class remarks for which side of the asymmetry this is on. The
    /// exception is logged, because an institution whose subscriptions are being refused by an
    /// outage needs to be able to tell that from a missing card.
    /// </para>
    /// </summary>
    public async Task<Result> EnsureMayDistributeAsync(
        Guid tenantId, Guid userId, Guid? productLineId, CancellationToken ct)
    {
        bool required;

        try
        {
            required = await RequiresCredentialAsync(tenantId, productLineId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not knowing whether a line is insurance is not a reason to let a contract through:
            // this is the gate in front of an insurer, and the agent can retry.
            logger.LogError(ex,
                "Could not determine whether product line {ProductLineId} is an insurance line "
                + "(tenant {TenantId}); the subscription is refused rather than allowed through",
                productLineId, tenantId);

            return Result.Fail(CredentialRequired);
        }

        if (!required) return Result.Ok();

        DistributionEligibilitySnapshot eligibility;

        try
        {
            eligibility = await compliance.GetDistributionEligibilityAsync(
                tenantId, userId, Today(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not read the distribution credential of user {UserId} (tenant {TenantId}); "
                + "the subscription is refused rather than allowed through",
                userId, tenantId);

            return Result.Fail(CredentialRequired);
        }

        return eligibility.IsValid ? Result.Ok() : Result.Fail(CredentialRequired);
    }

    /// <summary>
    /// Narrows a dispatching pool to the agents who may present
    /// <paramref name="productLineId"/>. Returns <paramref name="candidateIds"/> unchanged for a
    /// non-insurance line — the overwhelming majority of dispatches, which must cost nothing.
    ///
    /// <para>
    /// On a failure of either lookup the pool is returned UNCHANGED and the failure is logged.
    /// That is the one place this guard is permissive, and it is the asymmetry the class remarks
    /// describe: an M12 outage must not empty every insurance pool and park every lead on
    /// <c>NO_AGENT_AVAILABLE</c> while the SLA clock runs. The subscription gate is what still
    /// refuses the contract at the end of that road.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> FilterEligibleAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> candidateIds,
        Guid? productLineId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);

        if (candidateIds.Count == 0) return candidateIds;

        try
        {
            if (!await RequiresCredentialAsync(tenantId, productLineId, ct))
                return candidateIds;

            var eligibilities = await compliance.GetDistributionEligibilitiesAsync(
                tenantId, candidateIds, Today(), ct);

            var eligible = candidateIds
                .Where(id => eligibilities.TryGetValue(id, out var e) && e.IsValid)
                .ToList();

            if (eligible.Count < candidateIds.Count)
            {
                // Logged at information, not warning: on a line that requires a card, excluding
                // the agents who do not hold one is the rule working. What an operator needs is
                // to be able to explain a shrunken pool.
                logger.LogInformation(
                    "Insurance line {ProductLineId}: {Eligible} of {Candidates} agents hold a "
                    + "valid professional card (tenant {TenantId})",
                    productLineId, eligible.Count, candidateIds.Count, tenantId);
            }

            return eligible;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not filter the dispatch pool on professional cards for product line "
                + "{ProductLineId} (tenant {TenantId}); the pool is left unchanged and the "
                + "subscription gate still applies",
                productLineId, tenantId);

            return candidateIds;
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
