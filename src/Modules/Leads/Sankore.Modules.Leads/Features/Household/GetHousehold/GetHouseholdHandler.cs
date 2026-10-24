namespace Sankore.Modules.Leads.Features.Household.GetHousehold;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetHouseholdHandler(
    LeadsDbContext db,
    IAdministrationModule administration,
    LeadAccessGuard accessGuard,
    TimeProvider clock,
    IInstitutionCurrency? institutionCurrency = null)
    : IRequestHandler<GetHouseholdQuery, Result<LeadHouseholdDto>>
{
    public async Task<Result<LeadHouseholdDto>> Handle(
        GetHouseholdQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<LeadHouseholdDto>("LEAD_NOT_FOUND");

        // The SAME perimeter as the writes, and this read needs it more than most.
        //
        // Every other lead read in this module is scoped by the tenant query filter plus
        // `lead:read` and nothing else, and that is defensible for a phone number. It is not for
        // this endpoint: what it returns is a spouse's and children's identities — names, dates of
        // birth, addresses — and a PEP classification, which is the data this evolution encrypts
        // and permission-gates. Leaving the read open while AddHouseholdMember enforces ownership
        // would mean any colleague can SEE the household they cannot EDIT, which makes the write
        // guard decorative.
        //
        // Safe on this path despite failing closed: EnsureCanWorkAsync checks the bypass roles and
        // direct ownership BEFORE it asks M12 about supervision, so a lead's own holder never
        // reaches the cross-module call. Only a would-be supervisor is refused during an M12
        // outage, and refusing is right there — supervision is exactly what cannot be verified.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<LeadHouseholdDto>(access.Error!);

        // The screen needs to know WHY the add buttons are absent rather than letting the agent
        // discover it on a refused write. A lead with no resolved product line is editable with no
        // limits — see HouseholdPolicyResolver.
        BeneficiaryPolicyDto? policy = null;
        if (await ProductLines.ProductLineLookup.ForLeadAsync(administration, lead, ct) is { } lineId)
        {
            try
            {
                policy = await administration.GetBeneficiaryPolicyAsync(lead.TenantId, lineId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A read, so it degrades rather than fails: the household is shown and the screen
                // falls back to "enabled", which the write path will correct if it is not.
                policy = null;
            }
        }

        var today     = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var principal = lead.PrincipalIdentity;

        var members = lead.HouseholdMembers
            // Active first, then most recently added — a removed spouse must stay visible without
            // sitting above the current one.
            .OrderByDescending(m => m.IsActive)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m =>
            {
                // RESOLVED, never the stored identity: a member inheriting the principal's address
                // stores none, and showing that reads as missing data rather than as inheritance.
                var identity = m.ResolveIdentity(principal);

                return new HouseholdMemberDto(
                    MemberId:             m.Id,
                    Type:                 m.Type.ToString(),
                    SpouseUnionType:      m.SpouseUnionType?.ToString(),
                    UsesPrincipalAddress: m.UsesPrincipalAddress,
                    Identity:             HouseholdMapping.ToDto(identity),
                    Age:                  identity.AgeAt(today),
                    // The FLAG is visible to anyone who can see the lead; the DETAIL is not.
                    IsPepExposed:         m.Pep.IsPep,
                    Pep:                  query.IncludePepDetail ? HouseholdMapping.ToDto(m.Pep) : null,
                    IsActive:             m.IsActive,
                    CreatedAt:            m.CreatedAt,
                    RemovedAt:            m.RemovedAt,
                    RemovalReason:        m.RemovalReason);
            })
            .ToList();

        // Resources declared before they carried a currency are in the institution's.
        var resourcesCurrency = lead.HouseholdResourcesCurrency
            ?? (lead.HouseholdResourcesDeclaredAt is null ? null : await institutionCurrency.GetOrDefaultAsync(lead.TenantId, ct));

        return Result.Ok(new LeadHouseholdDto(
            LeadId:                lead.Id,
            MaxChildren:           policy?.MaxChildren,
            ChildMaxAge:           policy?.ChildMaxAge,
            Principal:             principal is null ? null : HouseholdMapping.ToDto(principal),
            PrincipalIsPepExposed: lead.PrincipalPep.IsPep,
            PrincipalPep:          query.IncludePepDetail
                                       ? HouseholdMapping.ToDto(lead.PrincipalPep)
                                       : null,
            HasPepExposure:        lead.HasPepExposure,
            Members:               members,
            Resources:             lead.HouseholdResourcesDeclaredAt is { } declaredAt
                                       ? new HouseholdResourcesDto(
                                           MonthlyIncome: lead.HouseholdMonthlyIncome,
                                           HousingCost:   lead.HouseholdHousingCost,
                                           OtherDebts:    lead.HouseholdOtherDebts,
                                           Currency:      resourcesCurrency!,
                                           DeclaredAt:    declaredAt)
                                       : null));
    }
}
