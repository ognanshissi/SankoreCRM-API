namespace Sankore.Modules.Kyc.Features.Files.Consumers;

using MassTransit;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.CreateKycFile;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// First of the two automatic triggers of KYC-B-01: M01 publishes <see cref="ClientCreatedEvent"/>
/// for EVERY client, so no customer can come into existence without a KYC file — whichever screen,
/// import or conversion created it.
///
/// <para>
/// A converted lead also produces <c>KycRequestedIntegrationEvent</c>, and both consumers send the
/// same command for the same customer. That race is not avoided here, it is delegated:
/// <c>CreateKycFileHandler</c> is idempotent and the loser gets <c>AlreadyExisted: true</c>.
/// Adding a guard of our own would only make the two paths disagree.
/// </para>
/// </summary>
public sealed class ClientCreatedKycConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientCreatedKycConsumer> logger)
    : IConsumer<ClientCreatedEvent>
{
    public async Task Consume(ConsumeContext<ClientCreatedEvent> context)
    {
        var evt = context.Message;

        // SYSTEM placeholder tenant: nothing to open for a tenant that does not exist.
        if (evt.TenantId == Guid.Empty) return;

        // Derived, not guessed. SourceLeadId is set by M01 exactly when the client record came
        // out of a lead conversion, which is the one piece of provenance the event carries — and
        // the channel is evidence an auditor reads, so it must reflect what happened rather than
        // which consumer happened to run.
        var channel = evt.SourceLeadId.HasValue ? KycChannel.LeadConversion : KycChannel.Agency;

        // The ambient scope must be established BEFORE any scoped service is resolved:
        // ITenantContext and ICurrentUser are built from it and a consumer has no HTTP context to
        // fall back on. Resolve the DbContext or ISender through the constructor instead and the
        // query filter silently binds to an empty tenant.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.CreatedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            // evt.CreatedBy, never a SYSTEM placeholder: the file has to carry the agent who is
            // accountable for completing it — see CreateKycFileCommand.InitiatedBy.
            var result = await sender.Send(
                new CreateKycFileCommand(
                    TenantId: evt.TenantId,
                    CustomerId: evt.ClientId,
                    Channel: channel,
                    InitiatedBy: evt.CreatedBy),
                context.CancellationToken);

            if (result.IsFailure)
            {
                // Swallowed on purpose. Throwing would hand the message back to the broker, and a
                // customer whose file cannot be opened will fail again on every redelivery — an
                // endless loop that helps nobody. The missing file is visible from the customer's
                // own 360° view and from the compliance backlog.
                logger.LogWarning(
                    "No KYC file opened for customer {CustomerId} (tenant {TenantId}): {Error}",
                    evt.ClientId, evt.TenantId, result.Error);
                return;
            }

            if (result.Value.AlreadyExisted)
            {
                // The normal outcome for a converted lead, not an incident: the other trigger won.
                logger.LogDebug(
                    "Customer {CustomerId} already had KYC file {KycFileId}",
                    evt.ClientId, result.Value.KycFileId);
                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId} opened for customer {CustomerId} on channel {Channel}",
                result.Value.KycFileId, evt.ClientId, channel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as the IsFailure branch, for the failures that arrive as exceptions
            // (a domain invariant, a broken event payload). A cancellation is the host shutting
            // down and must keep propagating so the message is redelivered rather than lost.
            logger.LogWarning(
                ex, "KYC file creation threw for customer {CustomerId} (tenant {TenantId})",
                evt.ClientId, evt.TenantId);
        }
    }
}
