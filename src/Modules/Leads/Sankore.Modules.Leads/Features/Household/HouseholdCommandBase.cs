namespace Sankore.Modules.Leads.Features.Household;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Everything a household write needs before it can touch the aggregate (BNF-B-03, BNF-B-05).
/// </summary>
/// <param name="Limits">From the product line's beneficiary policy.</param>
/// <param name="PepPostMandateMonths">
/// The tenant's window, from M12's compliance settings. Needed on EVERY write, not only the PEP
/// ones: adding or removing a member can flip <c>Lead.HasPepExposure</c>, and deciding that needs
/// the window.
/// </param>
internal sealed record HouseholdWriteScope(
    Lead Lead,
    HouseholdLimits Limits,
    int PepPostMandateMonths);

/// <summary>
/// The gates every household write runs, in the one order they have to happen.
///
/// <para>
/// Not inheritance for its own sake: each handler would otherwise repeat load → staleness →
/// ownership → policy → window, and the ORDER matters. Ownership before the policy, because
/// telling another agent's caller which product line a lead is on is already a leak; the policy
/// before the aggregate, because the child ceiling it carries is an argument the aggregate needs.
/// </para>
/// </summary>
internal sealed class HouseholdWriteContext(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    HouseholdPolicyResolver policyResolver,
    IAdministrationModule administration,
    Questionnaires.ExpectedResponsePlanner responsePlanner,
    // KEYED: the outbox publisher is registered per module, keyed on the DbContext type name, so
    // the row lands in leads.outbox_messages and commits with the lead.
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
{
    internal const string ComplianceUnavailable = "COMPLIANCE_SETTINGS_UNAVAILABLE";

    internal TimeProvider Clock => clock;

    /// <summary>
    /// Loads the lead WITH its household and runs the gates that apply to ANY write on it:
    /// staleness, ownership, and the tenant's PEP window. <see cref="HouseholdWriteScope.Limits"/>
    /// comes back <c>Unlimited</c> — use <see cref="LoadAsync"/> when the write touches the
    /// household itself and the product line's rules have to apply.
    /// </summary>
    /// <remarks>
    /// <c>Include</c> plus <c>AsTracking</c> is mandatory, not an optimisation: the one-spouse and
    /// max-children invariants are computed over <c>Lead.HouseholdMembers</c>, so a lead loaded
    /// without them would see an EMPTY household and happily add a second spouse — caught only by
    /// the unique index, as a database error with no usable code.
    /// </remarks>
    internal async Task<Result<HouseholdWriteScope>> LoadLeadAsync(
        Guid leadId, DateTimeOffset? expectedUpdatedAt, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == leadId, ct);

        if (lead is null)
            return Result.Fail<HouseholdWriteScope>("LEAD_NOT_FOUND");

        if (lead.IsStale(expectedUpdatedAt))
            return Result.Fail<HouseholdWriteScope>(LeadConcurrency.ConflictError);

        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<HouseholdWriteScope>(access.Error!);

        var window = await PepWindowAsync(lead.TenantId, ct);
        if (window.IsFailure)
            return Result.Fail<HouseholdWriteScope>(window.Error!);

        return Result.Ok(new HouseholdWriteScope(lead, HouseholdLimits.Unlimited, window.Value));
    }

    /// <summary>
    /// <see cref="LoadLeadAsync"/> plus the product line's household rules — what a write that
    /// TOUCHES the household needs.
    /// </summary>
    /// <remarks>
    /// Split from the load because the policy gate refuses outright
    /// (<c>HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE</c>) on a line that records no household — correct
    /// for adding a spouse, wrong for anything else. Lot 6's enrichment writes the principal's own
    /// identity and would have been refused on every current-account lead, which is why the two
    /// are now separate methods rather than one with a flag: a boolean gate is one a call site
    /// eventually passes the wrong way round.
    /// </remarks>
    internal async Task<Result<HouseholdWriteScope>> LoadAsync(
        Guid leadId, DateTimeOffset? expectedUpdatedAt, CancellationToken ct)
    {
        var loaded = await LoadLeadAsync(leadId, expectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return loaded;

        var scope = loaded.Value;

        var limits = await policyResolver.ResolveAsync(scope.Lead, ct);
        if (limits.IsFailure)
            return Result.Fail<HouseholdWriteScope>(limits.Error!);

        return Result.Ok(scope with { Limits = limits.Value });
    }

    /// <summary>
    /// The tenant's PEP post-mandate window (BNF-B-05).
    /// </summary>
    /// <remarks>
    /// FAILS CLOSED on an outage rather than falling back to the platform default of 12 months.
    /// A tenant that configured 24 would otherwise have members silently read as no longer
    /// exposed, and an LBC/FT flag computed from a guessed parameter is worse than a write the
    /// agent repeats.
    /// </remarks>
    internal async Task<Result<int>> PepWindowAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var settings = await administration.GetComplianceSettingsAsync(tenantId, ct);
            suppressed.Complete();

            return Result.Ok(settings.PepPostMandateMonths);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<int>(ComplianceUnavailable);
        }
    }

    /// <summary>
    /// Saves, publishing <c>LeadPepExposureChanged</c> through the outbox when the aggregate raised
    /// it — in the lead's own transaction, so the stored flag and the notification cannot disagree.
    /// </summary>
    internal async Task CommitAsync(Lead lead, CancellationToken ct)
    {
        foreach (var raised in lead.DomainEvents
                     .OfType<Domain.Events.LeadPepExposureChangedDomainEvent>())
        {
            await publisher.PublishAsync(
                new LeadPepExposureChangedIntegrationEvent(
                    lead.TenantId, lead.Id, raised.HasPepExposure),
                ct);
        }

        // Creates the questionnaires the household now owes (BNF-B-08) IN THIS TRANSACTION. A
        // member added in one transaction and its health questionnaire created in another is a
        // window where the member exists and is expected to answer nothing — which is exactly the
        // "insured person forgotten" the story exists to prevent.
        //
        // Removal needs no counterpart: a removed member's expectation falls out of scope because
        // the planner reads ACTIVE members, and its stored response is kept (BNF-B-08).
        await responsePlanner.SyncAsync(lead, ct);

        await db.SaveChangesAsync(ct);
    }
}
