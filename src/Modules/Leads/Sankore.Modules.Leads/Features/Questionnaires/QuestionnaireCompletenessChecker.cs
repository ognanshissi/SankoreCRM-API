namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>One questionnaire a person — or the project's file — still owes (BNF-B-08).</summary>
/// <param name="SubjectLabel">
/// Who owes it, in a form a screen can show without a second query — "Le dossier", "Le titulaire",
/// "L'enfant Kofi". BNF-B-04 wants the missing items listed PER PERSON, and a bare id is not that.
/// </param>
public sealed record MissingQuestionnaire(
    Guid TemplateId,
    string TemplateName,
    bool IsHealthQuestionnaire,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel);

/// <summary>One expected questionnaire of a project, with the response that answers it, if any.</summary>
/// <param name="InheritedFromLead">
/// The response was given on the lead itself — before projects held questionnaires, or with the
/// lead's capture — and counts for every project of that lead.
/// </param>
internal sealed record ProjectQuestionnaireState(
    ExpectedResponse Expected,
    QualificationResponse? Response,
    bool InheritedFromLead)
{
    public bool IsAnswered => Response?.Status == QualificationResponseStatus.Completed;
}

/// <summary>
/// Whether a project has every questionnaire its product asks answered — what the step marked
/// « Questionnaires complets », and winning the project, require.
/// </summary>
internal sealed class QuestionnaireCompletenessChecker(
    LeadsDbContext db,
    ExpectedResponsePlanner planner)
{
    /// <summary>
    /// The project's lead WITH its household, or null when it has none. The household is what the
    /// per-person plan reads: a lead loaded without it would owe nothing for its children.
    /// </summary>
    internal async Task<Lead?> LeadOfAsync(Opportunity project, CancellationToken ct)
        => project.LeadId is { } leadId
            ? await db.Leads
                .Include(l => l.HouseholdMembers)
                .FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;

    /// <summary>
    /// Every expected questionnaire of <paramref name="project"/>, each with the response that
    /// answers it: the project's own when it has one, else one given on the lead.
    /// </summary>
    /// <exception cref="ProductLineUnavailableException">The catalogue could not be read.</exception>
    internal async Task<IReadOnlyList<ProjectQuestionnaireState>> StatesAsync(
        Opportunity project, Lead? lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        var expected = await planner.PlanAsync(project, lead, ct);
        if (expected.Count == 0)
            return [];

        var responses = await db.QualificationResponses
            .Where(r => r.OpportunityId == project.Id
                     || (project.LeadId != null && r.OpportunityId == null && r.LeadId == project.LeadId))
            .ToListAsync(ct);

        return
        [
            .. expected.Select(e =>
            {
                bool Same(QualificationResponse r) =>
                    r.TemplateId == e.TemplateId && r.SubjectType == e.SubjectType && r.SubjectId == e.SubjectId;

                var own = responses.FirstOrDefault(r => r.OpportunityId == project.Id && Same(r));
                var inherited = responses.FirstOrDefault(r => r.OpportunityId == null && Same(r));

                // The project's own answer first; an answer on the lead counts when the project has
                // none — a ToComplete row of the project does not hide an answer given on the lead.
                if (own?.Status == QualificationResponseStatus.Completed)
                    return new ProjectQuestionnaireState(e, own, false);
                if (inherited?.Status == QualificationResponseStatus.Completed)
                    return new ProjectQuestionnaireState(e, inherited, true);
                return new ProjectQuestionnaireState(e, own, false);
            }),
        ];
    }

    /// <summary>
    /// The questionnaires still owed. Empty when the project is complete.
    /// </summary>
    /// <remarks>
    /// Computed from the PLAN rather than from the stored rows, so a questionnaire published after
    /// the household was recorded is still reported. A removed member owes nothing: the plan reads
    /// ACTIVE members only — the row stays, the expectation does not.
    /// </remarks>
    /// <exception cref="ProductLineUnavailableException">The catalogue could not be read.</exception>
    internal async Task<IReadOnlyList<MissingQuestionnaire>> MissingAsync(
        Opportunity project, Lead? lead, CancellationToken ct)
    {
        var outstanding = (await StatesAsync(project, lead, ct)).Where(s => !s.IsAnswered).ToList();
        if (outstanding.Count == 0)
            return [];

        var templateIds = outstanding.Select(o => o.Expected.TemplateId).Distinct().ToList();
        var names = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        return
        [
            .. outstanding.Select(o => new MissingQuestionnaire(
                TemplateId:            o.Expected.TemplateId,
                TemplateName:          names.TryGetValue(o.Expected.TemplateId, out var name) ? name : "—",
                IsHealthQuestionnaire: o.Expected.IsHealthData,
                SubjectType:           o.Expected.SubjectType.ToString(),
                SubjectId:             o.Expected.SubjectId,
                SubjectLabel:          QuestionnaireSubjectLabel.For(lead, o.Expected.SubjectType, o.Expected.SubjectId))),
        ];
    }
}

/// <summary>Who a questionnaire is about, as a screen says it.</summary>
internal static class QuestionnaireSubjectLabel
{
    internal const string Dossier = "Le dossier";

    internal static string For(Lead? lead, QualificationSubjectType type, Guid subjectId) => type switch
    {
        QualificationSubjectType.Lead => Dossier,
        QualificationSubjectType.Principal or QualificationSubjectType.CustomerPrincipal
            => Household.PersonLabel.Principal,
        _ => lead is null ? "Un membre du foyer" : Household.PersonLabel.For(lead, type, subjectId),
    };
}
