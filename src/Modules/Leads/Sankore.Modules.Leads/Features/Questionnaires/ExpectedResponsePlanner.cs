namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>One questionnaire a particular person is expected to answer (BNF-B-08).</summary>
internal sealed record ExpectedResponse(
    Guid TemplateId,
    int TemplateVersion,
    bool IsHealthData,
    QualificationSubjectType SubjectType,
    Guid SubjectId);

/// <summary>
/// Works out which questionnaires each person of a lead is expected to answer, and creates the
/// missing <c>ToComplete</c> rows (BNF-B-08).
///
/// <para>
/// <b>Why the system creates them rather than the screen asking.</b> The story's point is that an
/// insured person is never forgotten: adding a child to a household has to produce that child's
/// health questionnaire without anybody remembering to. A screen that enumerated the expectation
/// itself would get it right until a second screen existed.
/// </para>
///
/// <para>
/// <b>Idempotent by construction.</b> It diffs against what is already stored and inserts only
/// what is missing, so it is safe to run on every household change — and
/// <c>ux_qualification_responses_subject</c> is what makes it safe under two concurrent requests,
/// which no diff can be.
/// </para>
/// </summary>
internal sealed class ExpectedResponsePlanner(
    LeadsDbContext db,
    IAdministrationModule administration,
    TimeProvider clock)
{
    /// <summary>
    /// Creates the responses <paramref name="lead"/> is missing. Returns how many were added.
    /// </summary>
    /// <remarks>
    /// Does NOT call SaveChanges — the caller commits, so the new rows land in the same
    /// transaction as the household change that caused them. A member added and its questionnaires
    /// created in two transactions is a window in which the member exists and is not expected to
    /// answer anything.
    /// </remarks>
    internal async Task<int> SyncAsync(Lead lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var expected = await PlanAsync(lead, ct);
        if (expected.Count == 0)
            return 0;

        var existing = await db.QualificationResponses
            .Where(r => r.LeadId == lead.Id)
            .Select(r => new { r.TemplateId, r.SubjectType, r.SubjectId })
            .ToListAsync(ct);

        var already = existing
            .Select(e => (e.TemplateId, e.SubjectType, e.SubjectId))
            .ToHashSet();

        var now = clock.GetUtcNow();
        var added = 0;

        foreach (var want in expected)
        {
            if (!already.Add((want.TemplateId, want.SubjectType, want.SubjectId)))
                continue;

            db.QualificationResponses.Add(QualificationResponse.Expected(
                tenantId:             lead.TenantId,
                leadId:               lead.Id,
                templateId:           want.TemplateId,
                questionnaireVersion: want.TemplateVersion,
                subjectType:          want.SubjectType,
                subjectId:            want.SubjectId,
                isHealthData:         want.IsHealthData,
                now:                  now));

            added++;
        }

        return added;
    }

    /// <summary>
    /// The complete expectation for a lead: every per-person questionnaire of its product line,
    /// crossed with the people that questionnaire's scope names.
    /// </summary>
    internal async Task<IReadOnlyList<ExpectedResponse>> PlanAsync(Lead lead, CancellationToken ct)
    {
        // No resolved product line means no catalogue row, so no questionnaire can be attributed
        // to it. NOT an error: BNF-B-01 lets a lead be captured and its household collected before
        // anybody picks a product line, and the completeness check reports the gap instead.
        if (lead.ProductLineId is not { } lineId)
            return [];

        BeneficiaryPolicyDto? line;
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            line = await administration.GetBeneficiaryPolicyAsync(lead.TenantId, lineId, ct);
            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The CALLER decides what an outage means. Here it means "no expectation computed",
            // and the household write it accompanies still succeeds — a missing ToComplete row is
            // recreated on the next household change or by the completeness check, whereas failing
            // the write would lose the member.
            return [];
        }

        if (line is null)
            return [];   // a dangling product line id — see HouseholdPolicyResolver

        var templates = await db.QualificationTemplates
            .Where(t => t.Status == TemplateStatus.Published
                     && t.SubjectScope != QualificationSubjectScope.Lead
                     && (t.ProductCode == line.ProductCode
                      || (t.ProductCode == null && t.ProductCategory.ToString() == line.Category)))
            .Select(t => new
            {
                t.Id, t.Version, t.IsHealthQuestionnaire, t.SubjectScope
            })
            .ToListAsync(ct);

        if (templates.Count == 0)
            return [];

        var plan = new List<ExpectedResponse>();

        foreach (var template in templates)
        {
            foreach (var (subjectType, subjectId) in SubjectsFor(lead, template.SubjectScope))
            {
                plan.Add(new ExpectedResponse(
                    TemplateId:      template.Id,
                    // FROZEN here (BNF-B-07): the response records the version it was created
                    // against, so publishing a revision cannot re-interpret answers already given
                    // against questions the person was never asked.
                    TemplateVersion: template.Version,
                    IsHealthData:    template.IsHealthQuestionnaire,
                    SubjectType:     subjectType,
                    SubjectId:       subjectId));
            }
        }

        return plan;
    }

    /// <summary>
    /// The people a scope names. ACTIVE household members only — a removed member's expectation
    /// falls out of scope without its stored response being deleted (BNF-B-08).
    /// </summary>
    private static IEnumerable<(QualificationSubjectType Type, Guid Id)> SubjectsFor(
        Lead lead, QualificationSubjectScope scope)
    {
        // The principal has no id of its own: it IS the lead, so the lead's id identifies it.
        var principal = (QualificationSubjectType.Principal, lead.Id);

        switch (scope)
        {
            case QualificationSubjectScope.PrincipalOnly:
                yield return principal;
                break;

            case QualificationSubjectScope.SpouseOnly:
                foreach (var m in lead.ActiveHouseholdMembers
                             .Where(m => m.Type == HouseholdMemberType.Spouse))
                    yield return (QualificationSubjectType.HouseholdMember, m.Id);
                break;

            case QualificationSubjectScope.ChildrenOnly:
                foreach (var m in lead.ActiveHouseholdMembers
                             .Where(m => m.Type == HouseholdMemberType.Child))
                    yield return (QualificationSubjectType.HouseholdMember, m.Id);
                break;

            case QualificationSubjectScope.EachInsuredPerson:
                yield return principal;
                foreach (var m in lead.ActiveHouseholdMembers)
                    yield return (QualificationSubjectType.HouseholdMember, m.Id);
                break;

            case QualificationSubjectScope.Lead:
            default:
                // Not reachable: the query above excludes lead-scoped templates. Left explicit so
                // a new scope value cannot silently produce no subject at all.
                break;
        }
    }
}
