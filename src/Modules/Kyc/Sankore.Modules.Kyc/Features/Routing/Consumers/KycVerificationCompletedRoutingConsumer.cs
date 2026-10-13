namespace Sankore.Modules.Kyc.Features.Routing.Consumers;

using MassTransit;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Features.Routing.RouteRemoteFile;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// KRM-B-08's trigger: a verification finished, so a remotely collected file can be routed.
///
/// <para>
/// <b>Why the event and not the handler that produced it.</b> The story says "sur
/// KycVerificationCompleted", and the reason it is the right trigger is the replay: a file whose
/// biometric service was unreachable is left in <c>Verifying</c> and finished later by
/// <c>ReplayKycVerificationJob</c>, in a different process, on a different day. An in-process call
/// at the end of the submission would route the files that scored on the first attempt and quietly
/// never route the others — the ones that most need somebody to look at them.
/// </para>
///
/// <para>
/// <b>It consumes an event its own module publishes</b>, which is unusual here and is what makes
/// the decision durable: the event is written to M02's outbox in the verification's transaction,
/// so a routing that crashes is retried by the broker rather than lost between two commits.
/// </para>
///
/// <para>
/// <b>SYSTEM, and deliberately so.</b> The scope's actor is <c>Guid.Empty</c> — not the agent who
/// launched the verification, and not the file's responsible agent. A route is nobody's decision;
/// an audit row naming a person beside an automatic approval would be a signature they did not
/// give, which is the one thing this whole feature has to avoid claiming.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class KycVerificationCompletedRoutingConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycVerificationCompletedRoutingConsumer> logger)
    : IConsumer<KycVerificationCompletedEvent>
{
    public async Task Consume(ConsumeContext<KycVerificationCompletedEvent> context)
    {
        var evt = context.Message;

        if (evt.TenantId == Guid.Empty) return;

        // The ambient scope BEFORE the DI scope: ITenantContext and ICurrentUser are built from it
        // and a consumer has no HTTP context to fall back on.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            // The command decides whether there is anything to do: a file of another channel and a
            // file already routed both come back Routed = false. Filtering here instead would put
            // half the rule in a consumer and half in a handler, and the handler is the half that
            // has the file.
            var result = await sender.Send(
                new RouteRemoteKycFileCommand(
                    TenantId: evt.TenantId,
                    KycFileId: evt.KycFileId,
                    VerificationCompletedAt: evt.CompletedAt),
                context.CancellationToken);

            if (result.IsFailure)
            {
                // Logged and dropped rather than rethrown: every failure the handler can report is
                // one a redelivery would reproduce — a file of another tenant, a transition the
                // aggregate refuses. Looping it through the broker would retry it for ever while
                // the file sits in validation either way, which the compliance backlog shows.
                logger.LogError(
                    "KYC file {KycFileId} (tenant {TenantId}) could not be routed after its "
                    + "verification: {Error}",
                    evt.KycFileId, evt.TenantId, result.Error);

                return;
            }

            if (!result.Value.Routed) return;

            logger.LogInformation(
                "KYC file {KycFileId} routed after verification {Outcome}: {Decision}",
                evt.KycFileId, evt.Outcome,
                result.Value.Automatic ? "approved automatically" : result.Value.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation is the host shutting down and must keep propagating so the message is
            // redelivered. Anything else is an incident on our side: the file keeps its status and
            // an operator can see an unrouted remote file in validation.
            logger.LogError(ex,
                "Routing KYC file {KycFileId} (tenant {TenantId}) threw",
                evt.KycFileId, evt.TenantId);
        }
    }
}
