namespace Sankore.Modules.Leads.Features.Questionnaires.ProjectQuestionnaires;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Every questionnaire a project's product asks — of its file and of each person — with what has
/// been answered. One list, because one rule gates the step: all of it answered.
/// </summary>
/// <param name="IncludeHealthAnswers">
/// Whether the caller holds <c>lead:health:read</c>. Decided by the ENDPOINT from the caller's
/// claims — see <c>HealthAnswerAccess</c>.
/// </param>
internal sealed record GetProjectQuestionnairesQuery(Guid ProjectId, bool IncludeHealthAnswers)
    : IRequest<Result<ProjectQuestionnairesDto>>;

/// <param name="IsDossier">Asked once for the whole file — what the lead's « Qualification » tab asked.</param>
/// <param name="IsExpected">
/// False for an answer the product no longer asks (its product changed since): shown, never required.
/// </param>
/// <param name="IsMinor">A household member under 18 — the answers then need a respondent.</param>
/// <param name="HasHealthConsent">An active health consent covers this person (health questionnaires only).</param>
/// <param name="CanRecordHealthConsent">
/// False on a project with no lead: consents are kept on the lead, and a client file has no place
/// for one yet.
/// </param>
/// <param name="Status">« ToComplete » or « Completed ».</param>
/// <param name="InheritedFromLead">Answered on the lead before the project held it; counts here too.</param>
/// <param name="Answers">
/// Null when the caller may not read them — every health response unless they hold
/// <c>lead:health:read</c>, and every purged one. The STATUS is always visible.
/// </param>
public sealed record ProjectQuestionnaireItemDto(
    Guid TemplateId,
    string TemplateName,
    bool IsDossier,
    bool IsHealthQuestionnaire,
    bool IsExpected,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel,
    bool IsMinor,
    bool HasHealthConsent,
    bool CanRecordHealthConsent,
    string Status,
    Guid? ResponseId,
    bool InheritedFromLead,
    DateTimeOffset? AnsweredAt,
    int? ComputedScore,
    Guid? RespondentId,
    bool IsPurged,
    IReadOnlyList<HealthAnswer>? Answers,
    string? AnswersWithheldReason);

/// <summary>Who may answer for a minor: the principal or the spouse (BNF-B-08).</summary>
public sealed record QuestionnaireRespondentDto(Guid Id, string Label);

/// <param name="IsComplete">Every expected questionnaire answered — what the marked step checks.</param>
/// <param name="PlanUnavailable">
/// The catalogue could not be read, so what the product asks is unknown. The step stays refused
/// meanwhile; the screen says why instead of showing an empty, « complete » list.
/// </param>
public sealed record ProjectQuestionnairesDto(
    Guid ProjectId,
    Guid? LeadId,
    IReadOnlyList<ProjectQuestionnaireItemDto> Items,
    IReadOnlyList<QuestionnaireRespondentDto> Respondents,
    bool IsComplete,
    bool PlanUnavailable);

internal sealed class GetProjectQuestionnairesHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    QuestionnaireCompletenessChecker completeness,
    TimeProvider clock,
    LeadVisibility? visibility = null)
    : IRequestHandler<GetProjectQuestionnairesQuery, Result<ProjectQuestionnairesDto>>
{
    public async Task<Result<ProjectQuestionnairesDto>> Handle(
        GetProjectQuestionnairesQuery query, CancellationToken ct)
    {
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        var project = await projects.FirstOrDefaultAsync(o => o.Id == query.ProjectId, ct);
        if (project is null)
            return Result.Fail<ProjectQuestionnairesDto>(Opportunities.OpportunityErrors.NotFound);

        var lead = await completeness.LeadOfAsync(project, ct);
        if (lead is not null)
        {
            // Same perimeter as the household read, for the same reason: what this returns can be a
            // health declaration about somebody's children.
            var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
            if (access.IsFailure)
                return Result.Fail<ProjectQuestionnairesDto>(access.Error!);
        }

        IReadOnlyList<ProjectQuestionnaireState> states;
        var planUnavailable = false;
        try
        {
            states = await completeness.StatesAsync(project, lead, ct);
        }
        catch (ProductLineUnavailableException)
        {
            states = [];
            planUnavailable = true;
        }

        // An answer the plan no longer asks — the product changed — stays visible, never required.
        var planned = states
            .Select(s => (s.Expected.TemplateId, s.Expected.SubjectType, s.Expected.SubjectId))
            .ToHashSet();
        var orphans = await db.QualificationResponses
            .Where(r => r.OpportunityId == project.Id
                     && r.Status == QualificationResponseStatus.Completed)
            .ToListAsync(ct);
        orphans = orphans.Where(r => !planned.Contains((r.TemplateId, r.SubjectType, r.SubjectId))).ToList();

        var templateIds = states.Select(s => s.Expected.TemplateId)
            .Concat(orphans.Select(o => o.TemplateId)).Distinct().ToList();
        var templates = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.SubjectScope })
            .ToDictionaryAsync(t => t.Id, ct);

        var consents = lead is null
            ? []
            : (await db.LeadConsents
                .Where(c => c.LeadId == lead.Id
                         && c.Type == ConsentType.HealthData
                         && c.Status == ConsentStatus.Active)
                .Select(c => new { c.SubjectType, c.SubjectId })
                .ToListAsync(ct))
              .Select(c => (c.SubjectType, c.SubjectId))
              .ToHashSet();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        ProjectQuestionnaireItemDto Item(
            Guid templateId, bool isDossier, bool isHealth, bool isExpected,
            QualificationSubjectType subjectType, Guid subjectId,
            QualificationResponse? response, bool inherited)
        {
            // THE one door to the answers, so the permission is applied in one place.
            var read = response is { Status: QualificationResponseStatus.Completed }
                ? HealthAnswerAccess.Read(response, query.IncludeHealthAnswers)
                : null;
            var member = subjectType == QualificationSubjectType.HouseholdMember
                ? lead?.HouseholdMembers.FirstOrDefault(m => m.Id == subjectId)
                : null;
            var answered = response?.Status == QualificationResponseStatus.Completed;

            return new ProjectQuestionnaireItemDto(
                TemplateId:             templateId,
                TemplateName:           templates.TryGetValue(templateId, out var t) ? t.Name : "—",
                IsDossier:              isDossier,
                IsHealthQuestionnaire:  isHealth,
                IsExpected:             isExpected,
                SubjectType:            subjectType.ToString(),
                SubjectId:              subjectId,
                SubjectLabel:           QuestionnaireSubjectLabel.For(lead, subjectType, subjectId),
                IsMinor:                member?.Identity.AgeAt(today) is < 18,
                HasHealthConsent:       consents.Contains((subjectType, subjectId)),
                CanRecordHealthConsent: lead is not null,
                Status:                 answered ? nameof(QualificationResponseStatus.Completed) : nameof(QualificationResponseStatus.ToComplete),
                ResponseId:             response?.Id,
                InheritedFromLead:      inherited,
                AnsweredAt:             answered ? response!.AnsweredAt : null,
                ComputedScore:          answered ? response!.ComputedScore : null,
                RespondentId:           answered ? response!.RespondentId : null,
                IsPurged:               response?.IsPurged ?? false,
                Answers:                read is { IsSuccess: true } ? read.Value.Answers : null,
                AnswersWithheldReason:  read is { IsFailure: true } ? read.Error : null);
        }

        var items = states
            .Select(s => Item(
                s.Expected.TemplateId, s.Expected.IsDossier, s.Expected.IsHealthData, true,
                s.Expected.SubjectType, s.Expected.SubjectId, s.Response, s.InheritedFromLead))
            .Concat(orphans.Select(o => Item(
                o.TemplateId,
                templates.TryGetValue(o.TemplateId, out var t) && t.SubjectScope == QualificationSubjectScope.Lead,
                o.IsHealthData, false, o.SubjectType, o.SubjectId, o, false)))
            .ToList();

        var respondents = new List<QuestionnaireRespondentDto>();
        if (lead is not null)
        {
            respondents.Add(new QuestionnaireRespondentDto(lead.Id, $"{lead.FullName} (titulaire)"));
            var spouse = lead.ActiveHouseholdMembers.FirstOrDefault(m => m.Type == HouseholdMemberType.Spouse);
            if (spouse is not null)
                respondents.Add(new QuestionnaireRespondentDto(spouse.Id, $"{PersonLabel.For(spouse)}"));
        }

        return Result.Ok(new ProjectQuestionnairesDto(
            ProjectId:       project.Id,
            LeadId:          project.LeadId,
            Items:           items,
            Respondents:     respondents,
            IsComplete:      !planUnavailable && states.All(s => s.IsAnswered),
            PlanUnavailable: planUnavailable));
    }
}
