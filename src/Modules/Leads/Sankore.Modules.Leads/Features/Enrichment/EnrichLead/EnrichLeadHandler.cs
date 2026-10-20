namespace Sankore.Modules.Leads.Features.Enrichment.EnrichLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment.Events;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class EnrichLeadHandler(
    HouseholdWriteContext context,
    LeadsDbContext db,
    IAdministrationModule administration,
    LeadCompletenessCalculator calculator,
    ICurrentUser currentUser,
    ISender sender,
    IOptions<LeadModuleSettings> settings,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<EnrichLeadCommand, Result<LeadCompletenessDto>>
{
    internal const string ActivityNotFound = "LEAD_ACTIVITY_NOT_FOUND";
    internal const string ProductLineNotFound = "PRODUCT_LINE_NOT_FOUND";

    public async Task<Result<LeadCompletenessDto>> Handle(
        EnrichLeadCommand cmd, CancellationToken ct)
    {
        // LoadLeadAsync and not LoadAsync: this writes the PRINCIPAL's identity, and the household
        // policy gate would refuse every lead on a line that records no household. See there.
        var loaded = await context.LoadLeadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail<LeadCompletenessDto>(loaded.Error!);

        var lead = loaded.Value.Lead;

        // The activity is traceability, so a wrong id has to be refused rather than dropped: an
        // event naming an activity that belongs to another lead is worse than one naming none.
        if (cmd.SourceActivityId is { } activityId)
        {
            var belongs = await db.LeadActivities
                .AnyAsync(a => a.Id == activityId && a.LeadId == lead.Id, ct);

            if (!belongs)
                return Result.Fail<LeadCompletenessDto>(ActivityNotFound);
        }

        var lineChanged = false;

        if (cmd.ClearProductLine)
        {
            var cleared = lead.SetProductLine(null);
            if (cleared.IsFailure)
                return Result.Fail<LeadCompletenessDto>(cleared.Error!);

            lineChanged = true;
        }
        else if (cmd.ProductLineId is { } lineId && lineId != lead.ProductLineId)
        {
            var exists = await LineExistsAsync(lead.TenantId, lineId, ct);
            if (exists.IsFailure)
                return Result.Fail<LeadCompletenessDto>(exists.Error!);

            var set = lead.SetProductLine(lineId);
            if (set.IsFailure)
                return Result.Fail<LeadCompletenessDto>(set.Error!);

            lineChanged = true;
        }

        var enriched = lead.EnrichPrincipalIdentity(
            firstName:   cmd.FirstName,
            lastName:    cmd.LastName,
            usageName:   cmd.UsageName,
            birthDate:   cmd.BirthDate,
            gender:      ParseGender(cmd.Gender),
            addressLine: cmd.AddressLine,
            postalCode:  cmd.PostalCode,
            city:        cmd.City,
            countryCode: cmd.CountryCode,
            now:         context.Clock.GetUtcNow(),
            neighbourhood: cmd.Neighbourhood,
            landmark:    cmd.Landmark);

        if (enriched.IsFailure)
            return Result.Fail<LeadCompletenessDto>(enriched.Error!);

        if (cmd.SourceActivityId is { } confirmedBy)
        {
            var attached = lead.AttachIdentitySource(confirmedBy, context.Clock.GetUtcNow());
            if (attached.IsFailure)
                return Result.Fail<LeadCompletenessDto>(attached.Error!);
        }

        if (cmd.NameVerified)
        {
            var verified = lead.MarkNameVerified(
                currentUser.Id == Guid.Empty ? null : currentUser.Id, context.Clock.GetUtcNow());
            if (verified.IsFailure)
                return Result.Fail<LeadCompletenessDto>(verified.Error!);
        }

        // Ids and field NAMES only — never a value. An enrichment event carries exactly the
        // address and birth date the module spent a migration encrypting, and an outbox row is a
        // second, unencrypted home for them that nothing would ever clean up.
        await publisher.PublishAsync(
            new LeadEnrichedIntegrationEvent(
                TenantId:         lead.TenantId,
                LeadId:           lead.Id,
                ProductLineId:    lead.ProductLineId,
                ProductLineChanged: lineChanged,
                ChangedFields:    ChangedFieldNames(cmd),
                SourceActivityId: cmd.SourceActivityId),
            ct);

        // CommitAsync, not SaveChanges: resolving a product line is exactly what makes a
        // questionnaire owed, and the planner has to create those rows in THIS transaction (see
        // HouseholdWriteContext.CommitAsync). Without it a lead would be reported complete until
        // somebody next touched its household.
        await context.CommitAsync(lead, ct);

        // The address is worth points (LeadScoreCalculator.AddressScore), and the identity screen
        // tells the agent that completing it is what makes the lead dispatchable: the score has to
        // move when they save, not at the next logged activity. Same switch and same closed-lead
        // rule as LogActivityHandler.
        if (settings.Value.EnableAutoScoreRecalculation && AffectsScore(cmd)
            && lead.Status is not (LeadStatus.Converted or LeadStatus.Archived
                                 or LeadStatus.Lost or LeadStatus.Disqualified))
        {
            await sender.Send(new RecalculateLeadScoreCommand(lead.Id, "LEAD_ENRICHED"), ct);
        }

        // Recomputed AFTER the commit, so the percentage counts the ToComplete rows the line
        // change just produced.
        return Result.Ok(await calculator.ComputeAsync(lead, ct));
    }

    /// <summary>
    /// Whether the line exists in this tenant's catalogue.
    /// </summary>
    /// <remarks>
    /// A null policy means the line does not exist — <c>GetBeneficiaryPolicyAsync</c> distinguishes
    /// that from a line that exists and collects nothing, which is the whole reason it is nullable.
    ///
    /// <para>
    /// An M12 outage REFUSES the write rather than storing the id unvalidated. Same posture as
    /// <c>HouseholdPolicyResolver</c> and for a stronger reason: this field decides which policy
    /// governs the lead, and an unvalidated one is precisely what keeping it off the capture
    /// contracts exists to prevent.
    /// </para>
    /// </remarks>
    private async Task<Result> LineExistsAsync(Guid tenantId, Guid lineId, CancellationToken ct)
    {
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var policy = await administration.GetBeneficiaryPolicyAsync(tenantId, lineId, ct);
            suppressed.Complete();

            return policy is null ? Result.Fail(ProductLineNotFound) : Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail(HouseholdPolicyResolver.PolicyUnavailable);
        }
    }

    /// <summary>Whether the request touched a field <c>LeadScoreCalculator</c> reads.</summary>
    private static bool AffectsScore(EnrichLeadCommand cmd)
        => cmd.FirstName is not null || cmd.LastName is not null || cmd.BirthDate is not null
           || cmd.CountryCode is not null || cmd.City is not null
           || cmd.Neighbourhood is not null || cmd.Landmark is not null;

    private static LeadGender? ParseGender(string? raw)
        => Enum.TryParse<LeadGender>(raw, ignoreCase: true, out var gender) ? gender : null;

    /// <summary>
    /// Which fields the request carried, by NAME. What the audit trail and the event are allowed
    /// to say about an enrichment — the same rule as <c>AuditBehavior</c>.
    /// </summary>
    private static IReadOnlyList<string> ChangedFieldNames(EnrichLeadCommand cmd)
    {
        var names = new List<string>(12);

        if (cmd.FirstName   is not null) names.Add(nameof(cmd.FirstName));
        if (cmd.LastName    is not null) names.Add(nameof(cmd.LastName));
        if (cmd.UsageName   is not null) names.Add(nameof(cmd.UsageName));
        if (cmd.BirthDate   is not null) names.Add(nameof(cmd.BirthDate));
        if (cmd.Gender      is not null) names.Add(nameof(cmd.Gender));
        if (cmd.AddressLine is not null) names.Add(nameof(cmd.AddressLine));
        if (cmd.PostalCode  is not null) names.Add(nameof(cmd.PostalCode));
        if (cmd.City        is not null) names.Add(nameof(cmd.City));
        if (cmd.CountryCode is not null) names.Add(nameof(cmd.CountryCode));
        if (cmd.Neighbourhood is not null) names.Add(nameof(cmd.Neighbourhood));
        if (cmd.Landmark    is not null) names.Add(nameof(cmd.Landmark));
        if (cmd.NameVerified)            names.Add(nameof(cmd.NameVerified));

        return names;
    }
}
