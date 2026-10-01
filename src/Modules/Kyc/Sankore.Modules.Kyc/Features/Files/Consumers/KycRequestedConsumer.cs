namespace Sankore.Modules.Kyc.Features.Files.Consumers;

using MassTransit;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.CreateKycFile;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Second automatic trigger of KYC-B-01: M13 publishes <see cref="KycRequestedIntegrationEvent"/>
/// from <c>ConvertLeadHandler</c> when a lead becomes a customer.
///
/// <para>
/// Redundant with <see cref="ClientCreatedKycConsumer"/> by design — a converted lead produces
/// both events for the same customer — and the redundancy is the point: the conversion must open
/// a file even on the day M01's own event is delayed, lost or filtered. The duplicate costs
/// nothing because <c>CreateKycFileHandler</c> is idempotent; the loser simply reports
/// <c>AlreadyExisted: true</c>.
/// </para>
///
/// <para>
/// The event carries identity fields (name, phone, national id, date of birth). None of them is
/// used here: this slice only opens the file, and copying a sensitive value into this module
/// before the verification phase can encrypt it would create a second, unprotected home for it.
/// </para>
/// </summary>
public sealed class KycRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycRequestedConsumer> logger)
    : IConsumer<KycRequestedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<KycRequestedIntegrationEvent> context)
    {
        var evt = context.Message;

        // SYSTEM placeholder tenant: nothing to open for a tenant that does not exist.
        if (evt.TenantId == Guid.Empty) return;

        // The ambient scope must be established BEFORE any scoped service is resolved:
        // ITenantContext and ICurrentUser are built from it and a consumer has no HTTP context to
        // fall back on.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.RequestedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            // The channel is not derived here: this event exists only on the lead-conversion path,
            // so LeadConversion is a fact of the trigger, not an inference.
            // RequestedBy is the agent who converted the lead — the accountable actor, never a
            // SYSTEM placeholder. See CreateKycFileCommand.InitiatedBy.
            var result = await sender.Send(
                new CreateKycFileCommand(
                    TenantId: evt.TenantId,
                    CustomerId: evt.CustomerEntityId,
                    Channel: KycChannel.LeadConversion,
                    InitiatedBy: evt.RequestedBy),
                context.CancellationToken);

            if (result.IsFailure)
            {
                // Swallowed on purpose: a lead that will never be acceptable would otherwise be
                // redelivered forever. The absent file shows up in the compliance backlog.
                logger.LogWarning(
                    "No KYC file opened for customer {CustomerId} converted from lead {LeadId} " +
                    "(tenant {TenantId}): {Error}",
                    evt.CustomerEntityId, evt.LeadId, evt.TenantId, result.Error);
                return;
            }

            if (result.Value.AlreadyExisted)
            {
                // ClientCreatedKycConsumer got there first — the expected outcome, not an incident.
                logger.LogDebug(
                    "Customer {CustomerId} already had KYC file {KycFileId}",
                    evt.CustomerEntityId, result.Value.KycFileId);
                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId} opened for customer {CustomerId} converted from lead {LeadId}",
                result.Value.KycFileId, evt.CustomerEntityId, evt.LeadId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation is the host shutting down and must keep propagating so the message is
            // redelivered; every other failure is logged and dropped, same reasoning as above.
            logger.LogWarning(
                ex, "KYC file creation threw for customer {CustomerId} (tenant {TenantId})",
                evt.CustomerEntityId, evt.TenantId);
        }
    }
}
