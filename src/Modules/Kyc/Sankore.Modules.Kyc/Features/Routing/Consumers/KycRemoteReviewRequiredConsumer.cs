namespace Sankore.Modules.Kyc.Features.Routing.Consumers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MassTransit;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// KRM-B-09 — tells a human that a remotely collected file is waiting for their decision.
///
/// <para>
/// <b>Why a consumer and not a line inside the routing handler.</b> <c>QueueEmailAsync</c> is
/// another module's table and is not part of M02's transaction, so a mail queued inside the
/// routing would be sent for a decision that then rolled back — or lost when the mail failed and
/// the routing was retried. The specification says so outright in §8: emails are sent from outbox
/// consumers with an idempotency key. The routing commits, the event goes through the outbox, and
/// this runs after.
/// </para>
///
/// <para>
/// <b>The payload names the customer and nothing about their identity.</b> KRM-B-09 asks for the
/// client's name, the product and the reason — a rendered body sits in a mail provider's delivery
/// log for weeks, so it carries the display name M01 already prints on every list screen and
/// stops there. No score, no flag, no field read on the document: the agent opens the file, where
/// those sit behind the permissions that guard them.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class KycRemoteReviewRequiredConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycRemoteReviewRequiredConsumer> logger)
    : IConsumer<KycRemoteDecidedEvent>
{
    internal const string TemplateKey = "kyc.remote-review-required";

    public async Task Consume(ConsumeContext<KycRemoteDecidedEvent> context)
    {
        var evt = context.Message;

        if (evt.TenantId == Guid.Empty) return;

        // An automatic approval has nobody to tell. The customer is told by
        // KycRemoteOutcomeConsumer, off the validation event, exactly as a manual approval is.
        if (evt.Automatic) return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var administration = scope.ServiceProvider.GetRequiredService<IAdministrationModule>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var links = scope.ServiceProvider.GetRequiredService<IKycRemoteLinkTokens>();
        var ct = context.CancellationToken;

        try
        {
            var file = await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == evt.TenantId && f.Id == evt.KycFileId)
                .Select(f => new { f.AgencyId, f.ProductLineId })
                .FirstOrDefaultAsync(ct);

            if (file is null)
            {
                logger.LogWarning(
                    "KYC file {KycFileId} was routed but no longer exists; no review notice sent",
                    evt.KycFileId);
                return;
            }

            // The cascade is M12's: the responsible agent when they are active and still attached
            // to this agency, otherwise the branch manager. A file with no agency cannot resolve
            // either — see below.
            var recipient = file.AgencyId is { } agencyId && agencyId != Guid.Empty
                ? await administration.ResolveAgencyRecipientAsync(
                    evt.TenantId, agencyId, evt.ResponsibleAgentId, ct)
                : null;

            if (recipient is null)
            {
                // Not an error, and not silent either. An agency with no manager whose agent has
                // left is a real configuration, and the file is still in the back-office backlog
                // where somebody will see it — a notification is a convenience, never the only way
                // work surfaces. Logged so the gap is diagnosable rather than mysterious.
                logger.LogWarning(
                    "KYC file {KycFileId} (tenant {TenantId}, agency {AgencyId}) was routed to a "
                    + "human but no recipient could be resolved; it waits in the agency backlog",
                    evt.KycFileId, evt.TenantId, file.AgencyId);
                return;
            }

            var tenant = await tenants.GetAsync(evt.TenantId);

            // The product NAME, from the same policy read the routing used — the only place a
            // product line id can be turned into something readable without M02 reaching into
            // M12's catalogue. Absent is tolerable: the agent is being told to open a file.
            var productName = file.ProductLineId is { } lineId
                ? (await administration.GetKycRemotePolicyAsync(evt.TenantId, lineId, ct))?.ProductName
                : null;

            var clientName = (await scope.ServiceProvider
                .GetRequiredService<ICustomersModule>()
                .GetClientSummaryAsync(evt.TenantId, evt.CustomerEntityId, ct))?.DisplayName;

            var queued = await administration.NotifyUserAsync(new NotifyUserRequest(
                TenantId: evt.TenantId,
                UserId: recipient.Value,
                TemplateKey: TemplateKey,
                // Keyed on the FILE and the reason. Not on the file alone: a complement the
                // customer sends comes back through the routing with a new reason, and that is a
                // second legitimate notice. Not on the event either — a redelivery must be silent.
                IdempotencyKey: $"{TemplateKey}-{evt.KycFileId}-{evt.RoutingReason}",
                TemplateData: new Dictionary<string, object>
                {
                    ["client_name"] = clientName ?? string.Empty,
                    ["product_name"] = productName ?? string.Empty,
                    // The CODE. The template renders it as-is and the back-office screen is what
                    // translates it (KRM-F-08) — a French sentence here would be a second
                    // translation nobody keeps in step with the first.
                    ["routing_reason"] = evt.RoutingReason ?? string.Empty,
                    ["backoffice_link"] = string.IsNullOrWhiteSpace(tenant?.Fqdn)
                        ? string.Empty
                        : links.BuildBackOfficeLink(tenant.Fqdn, evt.KycFileId),
                    ["kyc_file_id"] = evt.KycFileId,
                }), ct);

            if (!queued)
            {
                logger.LogWarning(
                    "The review notice for KYC file {KycFileId} could not be queued to user "
                    + "{UserId}; the file is still in the backlog",
                    evt.KycFileId, recipient.Value);
                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId} review notice sent to {UserId} ({Reason})",
                evt.KycFileId, recipient.Value, evt.RoutingReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The routing is already committed and is the record of the decision. A notification
            // that failed must not loop through the broker for ever — the SLA sweep
            // (KycRemoteReviewSlaJob) chases the same file a day later and is the safety net.
            logger.LogError(ex,
                "Sending the review notice for KYC file {KycFileId} (tenant {TenantId}) threw",
                evt.KycFileId, evt.TenantId);
        }
    }
}
