namespace Sankore.Modules.Administration.Features.InsuranceDistribution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Warns the institution's compliance administrators that a distributor piece — agrément,
/// garantie financière, RC pro — is about to expire: at 60, 30 and 7 days, and on the day it does.
/// Once expired, new insurance subscriptions are refused until the renewal is recorded
/// (product owner, 2026-10-25), so the reminders exist to make that refusal never a surprise.
///
/// <para>
/// One e-mail per (piece, threshold, administrator): the idempotency key makes a rerun of the same
/// day a no-op, and a renewal is a new piece with its own keys.
/// </para>
/// </summary>
public sealed class DistributorDocumentExpiryJob(
    IServiceScopeFactory scopeFactory,
    ILogger<DistributorDocumentExpiryJob> logger)
{
    public const string TemplateKey = "insurance.distributor-document-expiring";

    private static readonly int[] Thresholds = [60, 30, 7, 0];

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AdministrationDbContext>();
        var administration = sp.GetRequiredService<IAdministrationModule>();
        var today = DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var due = (await db.DistributorDocuments
                .IgnoreQueryFilters()
                .Where(d => d.TenantId == tenantId && d.SupersededAt == null && d.ExpiresOn != null)
                .ToListAsync(ct))
            .Select(d => (Document: d, DaysLeft: d.ExpiresOn!.Value.DayNumber - today.DayNumber))
            .Where(x => Thresholds.Contains(x.DaysLeft))
            .ToList();

        if (due.Count == 0) return;

        var recipients = await administration.ListUsersWithPermissionAsync(tenantId, Permissions.CanManageCompliance.Code, ct);
        if (recipients.Count == 0)
        {
            logger.LogWarning(
                "Tenant {TenantId}: {Count} distributor piece(s) near expiry and nobody holds {Permission}",
                tenantId, due.Count, Permissions.CanManageCompliance.Code);
            return;
        }

        foreach (var (document, daysLeft) in due)
        foreach (var userId in recipients)
        {
            await administration.NotifyUserAsync(new NotifyUserRequest(
                TenantId: tenantId,
                UserId: userId,
                TemplateKey: TemplateKey,
                IdempotencyKey: $"{TemplateKey}:{document.Id}:{daysLeft}:{userId}",
                TemplateData: new Dictionary<string, object>
                {
                    ["document_label"] = Label(document.Kind),
                    ["reference"] = document.Reference,
                    ["expires_on"] = document.ExpiresOn!.Value.ToString("dd/MM/yyyy"),
                    ["days_left"] = daysLeft,
                }), ct);
        }
    }

    private static string Label(DistributorDocumentKind kind) => kind switch
    {
        DistributorDocumentKind.Approval => "Agrément",
        DistributorDocumentKind.FinancialGuarantee => "Garantie financière",
        _ => "Attestation de responsabilité civile professionnelle",
    };
}
