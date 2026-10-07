namespace Sankore.Modules.Workflow.Infrastructure.Seeders;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Gives every active tenant the built-in workflow templates other modules ask for by name.
///
/// <para>
/// It exists because <c>IWorkflowModule.StartWorkflowAsync</c> resolves a template by
/// <c>EntityType</c> and fails when none is active — and M02 has been calling it for the
/// <c>KycFile</c> entity type since the approval circuit was written, against a template nothing
/// ever created. The call degraded to a log line, so the gap was invisible: every KYC file reached
/// validation with no instance behind it.
/// </para>
///
/// <para>
/// Seeds only for a tenant that has <b>no template at all</b> for the entity type — active or not.
/// The rule M13's <c>DispatchingRuleSeeder</c> already applies: a tenant that configured its own has
/// made its choice, and re-creating a template after a deliberate deletion would be a start-up
/// undoing an administrator's decision. Deactivating one is also a decision, which is why the check
/// deliberately ignores <c>IsActive</c>.
/// </para>
/// </summary>
internal static class WorkflowTemplateSeeder
{
    /// <summary>
    /// The entity type M02 passes to <c>StartWorkflowAsync</c>. A string and not a shared constant:
    /// the two modules may not reference each other, and the contract travels as text by design.
    /// </summary>
    internal const string KycFileEntityType = "KycFile";

    /// <summary>
    /// Deadline per approval rung, in hours.
    ///
    /// <para>
    /// A SEED DEFAULT, not a policy: administrators change it through
    /// <c>PUT workflow/templates/{id}/steps/{stepId}</c> without a deployment. It is deliberately
    /// generous — 48 hours was the first proposal and it is short enough that an ordinary KYC file
    /// waiting on a branch manager over a weekend blows it. A blown deadline is terminal for the
    /// instance (see <c>SlaCheckerJob</c>), which costs the file its mirror for that round, so this
    /// value trades traceability for alerting and should be raised if files routinely sit longer.
    /// </para>
    /// </summary>
    internal const int ApprovalStepTimeoutHours = 48;

    /// <summary>
    /// The KYC approval ladder, in M02's order. The names are French because they are read by
    /// operators in the workflow screens; the role codes come from <see cref="Roles"/>.
    ///
    /// <para>
    /// <b>There is no ComplianceOfficer role.</b> M02's third rung is the compliance officer, and the
    /// closest seeded role is <c>RegulationManager</c>. It only gates decisions taken through M12's
    /// own screens — a decision mirrored from M02 performs no role check — so today it is a label
    /// rather than a control, but it is the line to fix the day a compliance role is added.
    /// </para>
    /// </summary>
    private static readonly (int Order, string Name, string Description, string RoleCode)[] KycSteps =
    [
        (1, "Agent",
            "Signature de l'agent qui a instruit le dossier.",
            "Agent"),
        (2, "Chef d'agence",
            "Deuxième paire d'yeux : vigilance standard ou élevée, ou capture refusée à répétition.",
            "BranchManager"),
        (3, "Responsable conformité",
            "Vigilance élevée ou doublon suspecté.",
            "RegulationManager"),
    ];

    public static async Task SeedAsync(
        WorkflowDbContext db, ITenantStore tenantStore, ILogger logger, CancellationToken ct = default)
    {
        var tenants = await tenantStore.GetAllActiveAsync(ct);
        if (tenants.Count == 0) return;

        // One read for every tenant: the seeder runs on every boot, and a query per tenant would
        // make start-up scale with the tenant count for a table that is almost always unchanged.
        var tenantsWithTemplate = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Where(t => t.EntityType == KycFileEntityType)
            .Select(t => t.TenantId)
            .Distinct()
            .ToListAsync(ct);

        var present = tenantsWithTemplate.ToHashSet();
        var seeded = 0;

        foreach (var tenant in tenants)
        {
            if (present.Contains(tenant.Id)) continue;

            db.WorkflowTemplates.Add(BuildKycTemplate(tenant.Id));
            seeded++;
        }

        if (seeded == 0) return;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Seeded the {EntityType} workflow template for {Count} tenant(s)",
            KycFileEntityType, seeded);
    }

    private static WorkflowTemplate BuildKycTemplate(Guid tenantId)
    {
        var template = WorkflowTemplate.Create(
            tenantId,
            KycFileEntityType,
            "Validation KYC",
            // SYSTEM: no person created this, and attributing it to one would put a real user's name
            // on a row they never touched. The same placeholder background jobs run under.
            createdByUserId: Guid.Empty,
            description:
                "Circuit de validation d'un dossier KYC. Les décisions sont prises dans le module KYC, "
                + "qui reste la référence — cette instance en est le reflet, pour la piste d'audit, "
                + "la corbeille « mes étapes » et les statistiques.");

        foreach (var (order, name, description, roleCode) in KycSteps)
            template.AddStep(order, name, description, roleCode, ApprovalStepTimeoutHours);

        // AFTER the steps: Activate refuses an empty template, and it clears and rebuilds the
        // transition table from the steps it finds — so activating first would leave a template with
        // no APPROVE path and an instance that could never advance.
        template.Activate();

        return template;
    }
}
