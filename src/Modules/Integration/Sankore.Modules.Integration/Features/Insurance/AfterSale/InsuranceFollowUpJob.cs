namespace Sankore.Modules.Integration.Features.Insurance.AfterSale;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// The insurance deadlines a person has to act on (lots 3 and 4), chased once a day:
/// <list type="bullet">
/// <item>the insurer's 15 days to answer a proposal — a reminder to chase it at day 10, and at day 15
/// the alert that its silence now counts as acceptance, which the agent confirms (never automatic);</item>
/// <item>a renunciation's refund, due 30 days after the request — at D-5 and on the day;</item>
/// <item>a bordereau's remittance, due 30 days after the oldest premium collected in the insurer's
/// name — at D-5 and on the day, to whoever may finalise statements.</item>
/// </list>
/// One e-mail per (item, threshold, recipient): the idempotency key makes a rerun a no-op.
/// </summary>
public sealed class InsuranceFollowUpJob(
    IServiceScopeFactory scopeFactory,
    ILogger<InsuranceFollowUpJob> logger)
{
    public const string TemplateKey = "insurance.follow-up";

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var administration = sp.GetRequiredService<IAdministrationModule>();
        var today = DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        async Task Notify(Guid userId, string key, string title, string message)
        {
            try
            {
                await administration.NotifyUserAsync(new NotifyUserRequest(
                    tenantId, userId, TemplateKey, $"{TemplateKey}:{key}:{userId}",
                    new Dictionary<string, object> { ["title"] = title, ["message"] = message }), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Insurance follow-up {Key} could not be queued", key);
            }
        }

        // ── The insurer's 15 days ───────────────────────────────────────────
        var waiting = (await db.InsuranceSubscriptions.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.SentToInsurerAt != null && s.IssuedAt == null && s.Underwriting == null)
                .ToListAsync(ct))
            .Where(s => !s.IsEnded);

        foreach (var s in waiting)
        {
            var days = today.DayNumber - DateOnly.FromDateTime(s.SentToInsurerAt!.Value.UtcDateTime).DayNumber;
            if (days == 10)
                await Notify(s.CreatedBy, $"insurer-reminder:{s.Id}", "Relancer l'assureur",
                    "Une proposition envoyée il y a 10 jours n'a pas de réponse de l'assureur. Il lui reste 5 jours.");
            else if (days == 15)
                await Notify(s.CreatedBy, $"insurer-silence:{s.Id}", "Silence de l'assureur",
                    "15 jours sans réponse : son silence vaut acceptation. Confirmez « réputé accepté » sur la souscription.");
        }

        // ── Renunciation refunds ────────────────────────────────────────────
        var refunds = (await db.PolicyOperations.IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && o.Kind == PolicyOperationKind.Renunciation && o.RefundDueBy != null)
                .ToListAsync(ct))
            .Where(o => o.IsOpen);

        foreach (var o in refunds)
        {
            var left = o.RefundDueBy!.Value.DayNumber - today.DayNumber;
            if (left is 5 or 0)
                await Notify(o.RequestedBy, $"renunciation:{o.Id}:{left}", "Remboursement de renonciation",
                    left == 0
                        ? $"Le remboursement de la renonciation est dû aujourd'hui ({o.RefundDueBy:dd/MM/yyyy}) par l'assureur."
                        : $"Le remboursement de la renonciation est dû le {o.RefundDueBy:dd/MM/yyyy}, dans 5 jours.");
        }

        // ── Remittances ─────────────────────────────────────────────────────
        var remittances = await db.InsurerStatements.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.RemittanceDueBy != null && s.RemittedOn == null)
            .ToListAsync(ct);

        var due = remittances
            .Select(s => (Statement: s, Left: s.RemittanceDueBy!.Value.DayNumber - today.DayNumber))
            .Where(x => x.Left is 5 or 0)
            .ToList();

        if (due.Count == 0) return;

        var recipients = await administration.ListUsersWithPermissionAsync(
            tenantId, Permissions.CanFinaliseInsuranceStatement.Code, ct);

        foreach (var (statement, left) in due)
        foreach (var userId in recipients)
            await Notify(userId, $"remittance:{statement.Id}:{left}", $"Reversement à l'assureur — bordereau {statement.Period}",
                left == 0
                    ? $"Les primes encaissées au nom de l'assureur doivent lui être reversées aujourd'hui ({statement.RemittanceDueBy:dd/MM/yyyy})."
                    : $"Les primes encaissées au nom de l'assureur doivent lui être reversées avant le {statement.RemittanceDueBy:dd/MM/yyyy}.");
    }
}
