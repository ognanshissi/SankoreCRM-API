namespace Sankore.Modules.Leads.Features.Opportunities.History;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>The fields an update may change, read before it: what <see cref="ProjectHistoryRecorder.Changes"/> compares.</summary>
internal sealed record ProjectHistorySnapshot(string Product, Money? Amount, DateTimeOffset? CloseDate)
{
    public static ProjectHistorySnapshot Of(Opportunity project)
        => new(project.Product, project.EstimatedAmount, project.ExpectedCloseDate);
}

/// <summary>
/// Writes a project's history (owner, 2026-10-27) next to the change, in the caller's save — so a
/// change rolled back leaves no line, and a line never describes a change that did not happen.
/// The author is the signed-in user; a job or a consumer, which acts for nobody, writes none.
///
/// <para>
/// Called by every handler that changes what the history shows: creation, the step move
/// (<c>ProjectStepTransition</c>, the one path of the step and phase endpoints), the update, the
/// closing, the version follow, the circuit restart and the contract opening. A new path that
/// moves, prices or closes a project must call it too.
/// </para>
/// </summary>
internal sealed class ProjectHistoryRecorder(LeadsDbContext db, ICurrentUser me, TimeProvider clock)
{
    private Guid? Actor => me.IsAuthenticated && me.Id != Guid.Empty ? me.Id : null;

    public void Created(Opportunity project)
        => Add(project, ProjectHistoryKind.Created, detail: project.Product);

    /// <summary>Nothing when the step does not change — a version follow that keeps its step, say.</summary>
    public void StepChanged(Opportunity project, string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        Add(project, ProjectHistoryKind.StepChanged, from, to);
    }

    /// <summary>The product, the amount and the close date, each compared with its value before the update.</summary>
    public void Changes(ProjectHistorySnapshot before, Opportunity project)
    {
        if (!string.Equals(before.Product, project.Product, StringComparison.Ordinal))
            Add(project, ProjectHistoryKind.ProductChanged, Blank(before.Product), Blank(project.Product));

        var after = project.EstimatedAmount;
        if (before.Amount?.Amount != after?.Amount || before.Amount?.Currency != after?.Currency)
            Add(project, ProjectHistoryKind.AmountChanged,
                Amount(before.Amount), Amount(after), detail: after?.Currency ?? before.Amount?.Currency);

        if (Day(before.CloseDate) != Day(project.ExpectedCloseDate))
            Add(project, ProjectHistoryKind.CloseDateChanged, Day(before.CloseDate), Day(project.ExpectedCloseDate));
    }

    public void Closed(Opportunity project)
        => Add(project,
            project.Stage == OpportunityStage.ClosedWon ? ProjectHistoryKind.Won : ProjectHistoryKind.Lost,
            detail: project.CloseReason);

    public void TemplateVersionChanged(Opportunity project, int? from, int? to, string templateName)
        => Add(project, ProjectHistoryKind.TemplateVersionChanged,
            from?.ToString(CultureInfo.InvariantCulture), to?.ToString(CultureInfo.InvariantCulture), templateName);

    public void WorkflowRestarted(Opportunity project, string? stepLabel, string? circuit)
        => Add(project, ProjectHistoryKind.WorkflowRestarted, to: stepLabel, detail: circuit);

    /// <summary>
    /// Once per contract, and saved here: the opening happens after the project's own save, and
    /// M01 answers the same contract when asked again — the line must not repeat with it.
    /// </summary>
    public async Task HoldingOpenedAsync(Opportunity project, Guid holdingId, CancellationToken ct)
    {
        var known = await db.ProjectHistory.AnyAsync(
            h => h.OpportunityId == project.Id && h.Kind == ProjectHistoryKind.HoldingOpened && h.RelatedId == holdingId, ct);
        if (known) return;

        Add(project, ProjectHistoryKind.HoldingOpened, relatedId: holdingId);
        await db.SaveChangesAsync(ct);
    }

    private void Add(
        Opportunity project, ProjectHistoryKind kind,
        string? from = null, string? to = null, string? detail = null, Guid? relatedId = null)
        => db.ProjectHistory.Add(ProjectHistoryEntry.Record(project, kind, Actor, clock.GetUtcNow(), from, to, detail, relatedId));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Amount(Money? money) => money?.Amount.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The day, as the screens show it: a close date is saved at noon UTC to stay on its day.</summary>
    private static string? Day(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
