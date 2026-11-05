namespace Sankore.Modules.Kyc.Features.Reviews;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// The bell for a KYC file that needs someone (2026-11-05, product owner): its responsible agent, or
/// whoever opened it when it has none (a file opened at the counter), when they are active and still
/// in the file's agency; else the branch manager. Nobody to tell is
/// not an error: the file stays in the agency's list, where the counters of « Ma journée » show it.
/// </summary>
internal sealed record ExpiringFile(Guid Id, Guid CustomerId, Guid? AgencyId, Guid? ResponsibleAgentId, Guid CreatedBy, DateOnly? Expiry);

internal static class KycAlerts
{
    public const string ReviewCategory = "kyc.review";
    public const string ExpiryCategory = "kyc.document-expiry";

    /// <summary>How long before a piece expires it is announced, and how long after it still is.</summary>
    public const int ExpiryWindowDays = 30;

    /// <summary>
    /// The files a piece can still matter on: validated, or under review. A List: on .NET 10 an array's
    /// Contains binds to the ReadOnlySpan extension and stops translating to SQL.
    /// </summary>
    public static readonly List<KycFileStatus> LiveStatuses = [KycFileStatus.Full, KycFileStatus.Simplified, KycFileStatus.UnderReview];

    public static async Task<Guid?> RecipientAsync(
        IAdministrationModule administration, Guid tenantId, Guid? agencyId, Guid? responsible, Guid createdBy, CancellationToken ct)
    {
        // SYSTEM opens a file with an empty id: nobody to address.
        var preferred = responsible ?? (createdBy == Guid.Empty ? null : createdBy);
        return agencyId is { } agency && agency != Guid.Empty
            ? await administration.ResolveAgencyRecipientAsync(tenantId, agency, preferred, ct)
            : preferred;
    }

    /// <summary>The live files whose latest expiry date falls in the window.</summary>
    internal static IQueryable<ExpiringFile> ExpiringFiles(KycDbContext db, Guid tenantId, DateOnly from, DateOnly to) =>
        db.KycIdentityDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.ExpiryDate != null)
            .GroupBy(d => d.KycFileId)
            .Select(g => new { FileId = g.Key, Expiry = g.Max(d => d.ExpiryDate) })
            .Where(x => x.Expiry >= from && x.Expiry <= to)
            .Join(db.KycFiles.IgnoreQueryFilters().Where(f => f.TenantId == tenantId && LiveStatuses.Contains(f.Status)),
                x => x.FileId, f => f.Id,
                (x, f) => new ExpiringFile(f.Id, f.CustomerId, f.AgencyId, f.ResponsibleAgentId, f.CreatedBy, x.Expiry));

    /// <summary>
    /// Pieces expiring soon or just expired, once for each. Reads the file's LATEST expiry date: a
    /// piece retaken since has nothing left to say. Sends directly rather than through the outbox:
    /// this moves no state, so there is no transaction for it to outlive, and the key makes the
    /// nightly run idempotent.
    /// </summary>
    public static async Task<int> SendExpiryAlertsAsync(
        KycDbContext db,
        INotificationsModule notifications,
        IAdministrationModule administration,
        ICustomersModule customers,
        Guid tenantId,
        DateOnly today,
        CancellationToken ct)
    {
        var from = today.AddDays(-ExpiryWindowDays);
        var to = today.AddDays(ExpiryWindowDays);

        var expiring = await ExpiringFiles(db, tenantId, from, to).ToListAsync(ct);
        if (expiring.Count == 0) return 0;

        var clients = await customers.GetClientSummariesAsync(
            tenantId, expiring.Select(f => f.CustomerId).Distinct().ToList(), ct);

        var count = 0;
        foreach (var file in expiring)
        {
            var expiry = file.Expiry!.Value;
            if (await RecipientAsync(administration, tenantId, file.AgencyId, file.ResponsibleAgentId, file.CreatedBy, ct) is not { } recipient)
                continue;

            var expired = expiry < today;
            var name = clients.GetValueOrDefault(file.CustomerId)?.DisplayName ?? "ce client";
            var sent = await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                tenantId, recipient, ExpiryCategory,
                expired ? "Pièce d'identité expirée" : "Pièce d'identité bientôt expirée",
                expired
                    ? $"La pièce de {name} a expiré : demandez-en une nouvelle."
                    : $"La pièce de {name} expire le {expiry:dd/MM/yyyy} : demandez-en une nouvelle.",
                $"/kyc/{file.Id}",
                $"kyc-document-{(expired ? "expired" : "expiring")}-{file.Id}-{expiry:yyyyMMdd}-{recipient}"), ct);
            if (sent.IsSuccess) count++;
        }
        return count;
    }
}

/// <summary>
/// Tells someone a review came due. <see cref="KycReviewDueEvent"/> was published with no consumer,
/// so a file moved to UnderReview without anybody knowing until its grace period ran out.
/// </summary>
// Public because MassTransit instantiates it.
public sealed class KycReviewDueNotificationConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycReviewDueNotificationConsumer> logger)
    : IConsumer<KycReviewDueEvent>
{
    public async Task Consume(ConsumeContext<KycReviewDueEvent> context)
    {
        var evt = context.Message;
        if (evt.TenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var ct = context.CancellationToken;

        var file = await sp.GetRequiredService<KycDbContext>().KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == evt.TenantId && f.Id == evt.KycFileId)
            .Select(f => new { f.AgencyId, f.ResponsibleAgentId, f.CreatedBy })
            .FirstOrDefaultAsync(ct);
        if (file is null) return;

        var recipient = await KycAlerts.RecipientAsync(
            sp.GetRequiredService<IAdministrationModule>(), evt.TenantId, file.AgencyId, file.ResponsibleAgentId, file.CreatedBy, ct);
        if (recipient is null)
        {
            logger.LogWarning(
                "KYC file {KycFileId} is due for review but nobody could be told; it waits in the agency's list.",
                evt.KycFileId);
            return;
        }

        var client = await sp.GetRequiredService<ICustomersModule>()
            .GetClientSummaryAsync(evt.TenantId, evt.CustomerEntityId, ct);

        await sp.GetRequiredService<INotificationsModule>().NotifyInAppAsync(new InAppNotificationRequest(
            evt.TenantId, recipient.Value, KycAlerts.ReviewCategory,
            "Révision KYC à faire",
            $"Le dossier de {client?.DisplayName ?? "ce client"} est à réviser.",
            $"/kyc/{evt.KycFileId}",
            $"kyc-review-due-{evt.KycFileId}-{evt.DueDate:yyyyMMdd}-{recipient}"), ct);
    }
}
