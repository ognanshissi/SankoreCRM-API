namespace Sankore.Modules.Leads.Features.Questionnaires.GetQuestionnaires;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadQuestionnairesHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    QuestionnaireCompletenessChecker completeness)
    : IRequestHandler<GetLeadQuestionnairesQuery, Result<LeadQuestionnairesDto>>
{
    public async Task<Result<LeadQuestionnairesDto>> Handle(
        GetLeadQuestionnairesQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<LeadQuestionnairesDto>("LEAD_NOT_FOUND");

        // Same perimeter as the household read, for the same reason: what this returns is a health
        // declaration about somebody's children.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<LeadQuestionnairesDto>(access.Error!);

        var responses = await db.QualificationResponses
            .Where(r => r.LeadId == lead.Id)
            .ToListAsync(ct);

        var templateIds = responses.Select(r => r.TemplateId).Distinct().ToList();
        var templates = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.IsHealthQuestionnaire })
            .ToListAsync(ct);
        var byId = templates.ToDictionary(t => t.Id);

        var missing = await completeness.MissingAsync(lead, ct);

        var dtos = responses
            .OrderBy(r => r.SubjectType)
            .ThenBy(r => r.CreatedAt)
            .Select(r =>
            {
                // THE one door to the answers, so the permission is applied in one place and the
                // architecture test has something to protect.
                var read = HealthAnswerAccess.Read(r, query.IncludeHealthAnswers);

                return new QuestionnaireResponseDto(
                    ResponseId:            r.Id,
                    TemplateId:            r.TemplateId,
                    TemplateName:          byId.TryGetValue(r.TemplateId, out var tpl) ? tpl.Name : "—",
                    IsHealthQuestionnaire: r.IsHealthData,
                    SubjectType:           r.SubjectType.ToString(),
                    SubjectId:             r.SubjectId,
                    SubjectLabel:          PersonLabel.For(lead, r.SubjectType, r.SubjectId),
                    QuestionnaireVersion:  r.QuestionnaireVersion,
                    Status:                r.Status.ToString(),
                    RespondentId:          r.RespondentId,
                    AnsweredAt:            r.AnsweredAt,
                    IsPurged:              r.IsPurged,
                    Answers:               read.IsSuccess ? read.Value.Answers : null,
                    AnswersWithheldReason: read.IsSuccess ? null : read.Error);
            })
            .ToList();

        return Result.Ok(new LeadQuestionnairesDto(
            LeadId:     lead.Id,
            Responses:  dtos,
            Missing:    missing,
            IsComplete: missing.Count == 0));
    }
}
