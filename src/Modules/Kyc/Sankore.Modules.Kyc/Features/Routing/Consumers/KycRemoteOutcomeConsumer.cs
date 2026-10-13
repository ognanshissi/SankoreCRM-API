namespace Sankore.Modules.Kyc.Features.Routing.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// KRM-B-10 — tells the CUSTOMER how their remotely deposited file ended up.
///
/// <para>
/// <b>It listens to the decision events, not to the routing.</b> A customer is owed a message
/// when their file is settled, and that happens in three different places: the automatic approval
/// (<c>RouteRemoteKycFileHandler</c>), the last rung of a human circuit, and a refusal from any
/// rung (<c>DecideKycApprovalHandler</c>). All three already publish <c>KycValidatedEvent</c> or
/// <c>KycRejectedEvent</c> through the outbox, so this consumer gets all three for free — and,
/// crucially, the automatic and the manual approval produce the SAME message. KRM-F-05 makes that
/// a requirement on the customer's side ("écran final identique quel que soit le mode de
/// validation") and it would be odd for the email to then give the mode away.
/// </para>
///
/// <para>
/// <b>Only remote files.</b> Both events are about every KYC file in the platform, most of which
/// were collected at a counter by an agent who will tell the customer themselves. The channel is
/// read off the file: a customer who never used a link is not emailed about one.
/// </para>
///
/// <para>
/// <b>What the messages may not contain</b>, from §8: no score, no flag, nothing read on the
/// document. The refusal carries no motive at all — the approver's comment travels on
/// <c>KycRejectedEvent</c> and is deliberately dropped here. It is written for operators, it can
/// name a document defect or a suspicion, and the customer is asked to call their agency instead.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class KycRemoteOutcomeConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycRemoteOutcomeConsumer> logger)
    : IConsumer<KycValidatedEvent>, IConsumer<KycRejectedEvent>
{
    internal const string ApprovedTemplate = "kyc.remote-approved";
    internal const string RejectedTemplate = "kyc.remote-rejected";

    public Task Consume(ConsumeContext<KycValidatedEvent> context)
        => NotifyAsync(
            context.Message.TenantId,
            context.Message.CustomerEntityId,
            ApprovedTemplate,
            context.CancellationToken);

    public Task Consume(ConsumeContext<KycRejectedEvent> context)
        => NotifyAsync(
            context.Message.TenantId,
            context.Message.CustomerEntityId,
            RejectedTemplate,
            context.CancellationToken);

    private async Task NotifyAsync(
        Guid tenantId, Guid clientId, string templateKey, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || clientId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var customers = scope.ServiceProvider.GetRequiredService<ICustomersModule>();
        var administration = scope.ServiceProvider.GetRequiredService<IAdministrationModule>();

        try
        {
            // The file the decision was about. Both events name the CLIENT and not the file —
            // their consumers elsewhere care about the customer's status, not about our
            // paperwork — so the channel is looked up here.
            //
            // The newest file of the customer, because a customer may have more than one over a
            // lifetime (a rejected one stays as evidence) and the decision that has just been
            // published is about the live one.
            var file = await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId && f.CustomerId == clientId)
                .OrderByDescending(f => f.UpdatedAt)
                .Select(f => new { f.Id, f.Channel, f.AgencyId, f.ProductLineId })
                .FirstOrDefaultAsync(ct);

            if (file is null || file.Channel != KycChannel.RemoteSelfService) return;

            var data = new Dictionary<string, object>();

            if (templateKey == ApprovedTemplate)
            {
                // The product they subscribed, so the message says what has been opened rather
                // than talking about a "dossier KYC" the customer never heard of.
                var productName = file.ProductLineId is { } lineId
                    ? (await administration.GetKycRemotePolicyAsync(tenantId, lineId, ct))?.ProductName
                    : null;

                data["product_name"] = productName ?? string.Empty;
            }
            else
            {
                // A refusal gives no motive and points at a human. The agency's name and its
                // phone number are what the customer needs — and the phone number does not exist
                // anywhere in this platform yet, which is noted on the template rather than
                // silently rendered as an empty line.
                var agency = file.AgencyId is { } agencyId && agencyId != Guid.Empty
                    ? await administration.GetAgencyAsync(tenantId, agencyId, ct)
                    : null;

                data["agency_name"] = agency?.Name ?? string.Empty;
                data["agency_phone"] = string.Empty;
            }

            // NotifyClientAsync fills client_first_name, full_name and company_name itself, and
            // never throws: a customer with no address, an address that will not decrypt and a
            // queue that refused all answer false.
            var queued = await customers.NotifyClientAsync(new NotifyClientRequest(
                TenantId: tenantId,
                ClientId: clientId,
                TemplateKey: templateKey,
                // Keyed on the FILE and the template. A file is validated once and refused once,
                // so this silences a redelivery without silencing the legitimate second message a
                // customer gets if a later review changes the outcome on a NEW file.
                IdempotencyKey: $"{templateKey}-{file.Id}",
                TemplateData: data), ct);

            if (!queued)
            {
                logger.LogWarning(
                    "The {Template} message for KYC file {KycFileId} could not be queued",
                    templateKey, file.Id);
                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId}: customer informed ({Template})", file.Id, templateKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The decision stands. A customer who was not emailed is a support call; a decision
            // retried through the broker because a mail failed would be a compliance record
            // rewritten by a mail server.
            logger.LogError(ex,
                "Informing client {ClientId} (tenant {TenantId}) of a remote KYC outcome threw",
                clientId, tenantId);
        }
    }
}
