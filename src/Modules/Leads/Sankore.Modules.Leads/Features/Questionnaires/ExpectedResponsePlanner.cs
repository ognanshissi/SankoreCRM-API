namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>One questionnaire a particular person — or the project's file — is expected to answer.</summary>
/// <param name="IsDossier">
/// True for the questionnaire asked once for the whole file (a lead-scoped template): the one the
/// lead's « Qualification » tab used to ask.
/// </param>
internal sealed record ExpectedResponse(
    Guid TemplateId,
    int TemplateVersion,
    bool IsHealthData,
    QualificationSubjectType SubjectType,
    Guid SubjectId,
    bool IsDossier = false);

/// <summary>
/// Works out which questionnaires a PROJECT owes, from its product, and creates the missing
/// <c>ToComplete</c> rows (BNF-B-08).
///
/// <para>
/// <b>The product of the project, not of the lead.</b> A lead can carry a credit and an insurance
/// at once; each asks its own questions, so each project is planned from its own product line.
/// Two kinds of questionnaire come out of it:
/// </para>
/// <list type="bullet">
/// <item>the FILE's questionnaire — one lead-scoped template, the most specific published one:
/// the product's own, else its family's, else the generic one;</item>
/// <item>every per-person questionnaire of the product line, crossed with the people its scope
/// names. On a project opened on a client with no lead there is no household: only the holder
/// is asked, as <see cref="QualificationSubjectType.CustomerPrincipal"/>.</item>
/// </list>
///
/// <para>
/// <b>Why the system creates rows rather than the screen asking.</b> The story's point is that an
/// insured person is never forgotten: adding a child to a household has to produce that child's
/// health questionnaire without anybody remembering to.
/// </para>
///
/// <para>
/// <b>Idempotent by construction.</b> It diffs against what is already stored and inserts only
/// what is missing — and <c>ux_qualification_responses_project_subject</c> is what makes it safe
/// under two concurrent requests, which no diff can be.
/// </para>
/// </summary>
internal sealed class ExpectedResponsePlanner(
    LeadsDbContext db,
    IAdministrationModule administration,
    TimeProvider clock)
{
    /// <summary>
    /// Creates the rows the OPEN projects of <paramref name="lead"/> are missing — what a household
    /// change calls. Returns how many were added.
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

        var projects = await db.Opportunities
            .Where(o => o.LeadId == lead.Id
                     && o.Stage != OpportunityStage.ClosedWon
                     && o.Stage != OpportunityStage.ClosedLost)
            .ToListAsync(ct);

        var added = 0;
        foreach (var project in projects)
            added += await SyncAsync(project, lead, ct);

        return added;
    }

    /// <summary>Creates the rows <paramref name="project"/> is missing. Same contract as above.</summary>
    /// <param name="lead">The project's lead, loaded WITH its household; null when it has none.</param>
    internal async Task<int> SyncAsync(Opportunity project, Lead? lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        IReadOnlyList<ExpectedResponse> expected;
        try
        {
            expected = await PlanAsync(project, lead, ct);
        }
        catch (ProductLineUnavailableException)
        {
            // A write never fails for want of the catalogue: the rows come on the next sync, and
            // the gate reads the plan itself.
            return 0;
        }

        if (expected.Count == 0)
            return 0;

        // Tracked rows too: a submission adds its response before syncing, and the database does
        // not have it yet.
        var existing = await db.QualificationResponses
            .Where(r => r.OpportunityId == project.Id)
            .Select(r => new { r.TemplateId, r.SubjectType, r.SubjectId })
            .ToListAsync(ct);

        var already = existing
            .Select(e => (e.TemplateId, e.SubjectType, e.SubjectId))
            .Concat(db.QualificationResponses.Local
                .Where(r => r.OpportunityId == project.Id)
                .Select(r => (r.TemplateId, r.SubjectType, r.SubjectId)))
            .ToHashSet();

        var now = clock.GetUtcNow();
        var added = 0;

        foreach (var want in expected)
        {
            if (!already.Add((want.TemplateId, want.SubjectType, want.SubjectId)))
                continue;

            db.QualificationResponses.Add(QualificationResponse.Expected(
                tenantId:             project.TenantId,
                leadId:               project.LeadId,
                templateId:           want.TemplateId,
                questionnaireVersion: want.TemplateVersion,
                subjectType:          want.SubjectType,
                subjectId:            want.SubjectId,
                isHealthData:         want.IsHealthData,
                now:                  now,
                opportunityId:        project.Id));

            added++;
        }

        return added;
    }

    /// <summary>
    /// Everything <paramref name="project"/> owes: the file's questionnaire, then the per-person
    /// ones of its product line.
    /// </summary>
    /// <param name="lead">The project's lead, loaded WITH its household; null when it has none.</param>
    internal async Task<IReadOnlyList<ExpectedResponse>> PlanAsync(
        Opportunity project, Lead? lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        var line = await LineOfAsync(project, ct);
        var code = line?.ProductCode?.Trim().ToUpperInvariant();
        var category = line?.Category;

        var plan = new List<ExpectedResponse>();

        // ── The file's questionnaire: the most specific lead-scoped template ─────
        var dossierTemplates = await db.QualificationTemplates
            .Where(t => t.Status == TemplateStatus.Published
                     && t.SubjectScope == QualificationSubjectScope.Lead)
            .Select(t => new { t.Id, t.Version, t.IsHealthQuestionnaire, t.ProductCode, t.ProductCategory })
            .ToListAsync(ct);

        var dossier =
            (code is null ? null : dossierTemplates.FirstOrDefault(t => t.ProductCode == code))
            ?? (category is null ? null : dossierTemplates.FirstOrDefault(
                t => t.ProductCode is null && t.ProductCategory.ToString() == category))
            ?? dossierTemplates.FirstOrDefault(t => t.ProductCode is null && t.ProductCategory is null);

        if (dossier is not null)
        {
            plan.Add(new ExpectedResponse(
                TemplateId:      dossier.Id,
                TemplateVersion: dossier.Version,
                IsHealthData:    dossier.IsHealthQuestionnaire,
                SubjectType:     QualificationSubjectType.Lead,
                SubjectId:       DossierSubjectId(project),
                IsDossier:       true));
        }

        // ── The per-person questionnaires of the product line ───────────────────
        //
        // No resolved product line means no catalogue row, so no per-person questionnaire can be
        // attributed to it. NOT an error: a project is opened on a need « à préciser » before
        // anybody picks a product.
        if (line is null)
            return plan;

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

        foreach (var template in templates)
        {
            foreach (var (subjectType, subjectId) in SubjectsFor(project, lead, template.SubjectScope))
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
    /// Who « the file » is, as a subject: the lead when there is one — so an answer given on the
    /// lead before projects held questionnaires still counts — else the project itself.
    /// </summary>
    internal static Guid DossierSubjectId(Opportunity project) => project.LeadId ?? project.Id;

    /// <summary>The product line the project's product names, or null.</summary>
    private async Task<BeneficiaryPolicyDto?> LineOfAsync(Opportunity project, CancellationToken ct)
    {
        if (project.ProductLineId is null && string.IsNullOrWhiteSpace(project.Product))
            return null;

        try
        {
            // Suppressed: M12 reads through its own DbContext, and a second connection enlisting
            // in a command's ambient scope promotes it to a distributed transaction Npgsql refuses.
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var lineId = project.ProductLineId
                ?? await administration.ResolveProductLineAsync(project.TenantId, project.Product, ct);
            var line = lineId is { } id
                ? await administration.GetBeneficiaryPolicyAsync(project.TenantId, id, ct)
                : null;

            suppressed.Complete();
            return line;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The CALLER decides what an outage means: a household write still succeeds (its rows
            // come on the next sync), the step gate refuses — an unread catalogue must not pass a
            // project as complete.
            _ = ex;
            throw new ProductLineUnavailableException();
        }
    }

    /// <summary>
    /// The people a scope names. ACTIVE household members only — a removed member's expectation
    /// falls out of scope without its stored response being deleted (BNF-B-08).
    /// </summary>
    private static IEnumerable<(QualificationSubjectType Type, Guid Id)> SubjectsFor(
        Opportunity project, Lead? lead, QualificationSubjectScope scope)
    {
        if (lead is null)
        {
            // A client with no lead has no household in this module: only its holder is asked.
            if (project.CustomerEntityId is { } customerId
                && scope is QualificationSubjectScope.PrincipalOnly or QualificationSubjectScope.EachInsuredPerson)
                yield return (QualificationSubjectType.CustomerPrincipal, customerId);
            yield break;
        }

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

/// <summary>
/// The catalogue could not be read, so the questionnaires a project owes are unknown. Callers that
/// write (a household change) swallow it; callers that GATE treat it as incomplete.
/// </summary>
internal sealed class ProductLineUnavailableException : Exception
{
    public ProductLineUnavailableException()
        : base("The product line of the project could not be read.") { }
}
