namespace Sankore.Modules.Leads.Features.Fatca.GetDeclarations;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Fatca;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetFatcaDeclarationsHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    FatcaSubjectPlanner planner)
    : IRequestHandler<GetFatcaDeclarationsQuery, Result<LeadFatcaDto>>
{
    internal const string DeclarationMissing = "FATCA_DECLARATION_MISSING";
    internal const string DocumentationOutstanding = "FATCA_DOCUMENTATION_OUTSTANDING";

    public async Task<Result<LeadFatcaDto>> Handle(
        GetFatcaDeclarationsQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<LeadFatcaDto>("LEAD_NOT_FOUND");

        // The same perimeter as the household and the questionnaires: a FATCA declaration names a
        // person's tax residence and, for a US person, their taxpayer identifier.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<LeadFatcaDto>(access.Error!);

        var scope = await planner.ResolveAsync(lead, ct);
        if (scope.IsFailure)
            return Result.Fail<LeadFatcaDto>(scope.Error!);

        var enabled = scope.Value.Enabled;

        var declarations = await db.FatcaDeclarations
            .Where(d => d.LeadId == lead.Id)
            .OrderByDescending(d => d.IsActive)
            .ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        var dtos = declarations
            .Select(d => new FatcaDeclarationDto(
                DeclarationId:            d.Id,
                SubjectType:              d.SubjectType.ToString(),
                SubjectId:                d.SubjectId,
                SubjectLabel:             Label(lead, d.SubjectType, d.SubjectId),
                // EFFECTIVE, not stored: NotApplicable when the tenant is not subject to FATCA.
                Status:                   d.EffectiveStatus(enabled).ToString(),
                Documentation:            d.Documentation.ToString(),
                DeclaredIndicia:          d.Indicia.Declared(),
                // Presence, never the value.
                HasUsTin:                 d.UsTin is not null,
                HasSelfCertification:     d.SelfCertificationDocRef is not null,
                HasEvidence:              d.EvidenceDocRef is not null,
                SignedAt:                 d.SignedAt,
                RequiresComplianceReview: enabled && d.RequiresComplianceReview,
                IsActive:                 d.IsActive,
                CreatedAt:                d.CreatedAt,
                SupersededAt:             d.SupersededAt))
            .ToList();

        // Nothing is expected when FATCA is off — BNF-B-11's "ni calculé dans la complétude".
        var missing = enabled
            ? Outstanding(lead, scope.Value, declarations)
            : [];

        return Result.Ok(new LeadFatcaDto(
            LeadId:               lead.Id,
            Enabled:              enabled,
            RequiredAtEnrichment: scope.Value.RequiredAtEnrichment,
            Declarations:         dtos,
            Missing:              missing,
            IsComplete:           missing.Count == 0));
    }

    /// <summary>
    /// People in scope who have not finished. Two distinct reasons, kept distinct because they lead
    /// to different actions: collect a declaration, or chase the documentation for one already made.
    /// </summary>
    private static List<MissingFatcaDeclaration> Outstanding(
        Lead lead, FatcaScope scope, List<FatcaDeclaration> declarations)
    {
        var active = declarations
            .Where(d => d.IsActive)
            .ToDictionary(d => (d.SubjectType, d.SubjectId));

        var missing = new List<MissingFatcaDeclaration>();

        foreach (var (type, id) in scope.Subjects)
        {
            if (!active.TryGetValue((type, id), out var declaration))
            {
                missing.Add(new MissingFatcaDeclaration(
                    type.ToString(), id, Label(lead, type, id), DeclarationMissing));
                continue;
            }

            if (declaration.IsDocumentationOutstanding)
            {
                missing.Add(new MissingFatcaDeclaration(
                    type.ToString(), id, Label(lead, type, id), DocumentationOutstanding));
            }
        }

        return missing;
    }

    /// <summary>The person's first name and nothing else — a label is not a place for an address.</summary>
    private static string Label(Lead lead, QualificationSubjectType type, Guid subjectId)
    {
        if (type is QualificationSubjectType.Principal or QualificationSubjectType.Lead)
            return "Le titulaire";

        var member = lead.HouseholdMembers.FirstOrDefault(m => m.Id == subjectId);
        if (member is null)
            return "Un membre du foyer";

        return member.Type == HouseholdMemberType.Spouse
            ? $"Le conjoint ({member.Identity.FirstName})"
            : $"L'enfant {member.Identity.FirstName}";
    }
}
