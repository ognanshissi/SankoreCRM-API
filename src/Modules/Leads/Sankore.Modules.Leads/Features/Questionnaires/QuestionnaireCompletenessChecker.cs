namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>One questionnaire a person still owes (BNF-B-08).</summary>
/// <param name="SubjectLabel">
/// Who owes it, in a form a screen can show without a second query — "Le titulaire", "Le conjoint",
/// "L'enfant Kofi". BNF-B-04 wants the missing items listed PER PERSON, and a bare id is not that.
/// </param>
public sealed record MissingQuestionnaire(
    Guid TemplateId,
    string TemplateName,
    bool IsHealthQuestionnaire,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel);

/// <summary>
/// Whether every questionnaire a lead's product line asks of its people has been answered
/// (BNF-B-08) — the gate that refuses a conversion with
/// <c>MISSING_QUESTIONNAIRE_RESPONSES</c>.
/// </summary>
internal sealed class QuestionnaireCompletenessChecker(
    LeadsDbContext db,
    ExpectedResponsePlanner planner)
{
    internal const string MissingResponses = "MISSING_QUESTIONNAIRE_RESPONSES";

    /// <summary>
    /// The questionnaires still owed, newest expectation first. Empty when the lead is complete.
    /// </summary>
    /// <remarks>
    /// Computed from the PLAN rather than from the stored rows, so a questionnaire published after
    /// the household was recorded is still reported — the stored <c>ToComplete</c> rows only exist
    /// for expectations that were known at the time somebody touched the household.
    ///
    /// <para>
    /// A removed member owes nothing: the plan reads ACTIVE members only, which is how BNF-B-08's
    /// "ses réponses passent hors périmètre de complétude, sans suppression" is implemented — the
    /// row stays, the expectation does not.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<MissingQuestionnaire>> MissingAsync(
        Lead lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var expected = await planner.PlanAsync(lead, ct);
        if (expected.Count == 0)
            return [];

        var answered = await db.QualificationResponses
            .Where(r => r.LeadId == lead.Id
                     && r.Status == QualificationResponseStatus.Completed)
            .Select(r => new { r.TemplateId, r.SubjectType, r.SubjectId })
            .ToListAsync(ct);

        var done = answered
            .Select(a => (a.TemplateId, a.SubjectType, a.SubjectId))
            .ToHashSet();

        var outstanding = expected
            .Where(e => !done.Contains((e.TemplateId, e.SubjectType, e.SubjectId)))
            .ToList();

        if (outstanding.Count == 0)
            return [];

        var templateIds = outstanding.Select(o => o.TemplateId).Distinct().ToList();
        var templateNames = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.IsHealthQuestionnaire })
            .ToListAsync(ct);

        var names = templateNames.ToDictionary(t => t.Id);

        return
        [
            .. outstanding.Select(o => new MissingQuestionnaire(
                TemplateId:            o.TemplateId,
                TemplateName:          names.TryGetValue(o.TemplateId, out var tpl) ? tpl.Name : "—",
                IsHealthQuestionnaire: o.IsHealthData,
                SubjectType:           o.SubjectType.ToString(),
                SubjectId:             o.SubjectId,
                SubjectLabel:          Features.Household.PersonLabel.For(
                                           lead, o.SubjectType, o.SubjectId)))
        ];
    }
}
