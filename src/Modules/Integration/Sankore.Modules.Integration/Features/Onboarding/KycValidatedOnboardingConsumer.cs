namespace Sankore.Modules.Integration.Features.Onboarding;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Step one of the onboarding chain (INT-14, criterion 1): an approved KYC file queues the
/// customer's creation in the core banking system.
///
/// <para>
/// A queued command and not a call: <c>RequestCustomerCreationAsync</c> adds the row to this
/// module's unit of work and the dispatcher of INT-06 delivers it out of band. A customer
/// validated at 23:00 while the CBS is down is still created when it comes back, and nothing
/// about this consumer's latency depends on a far-end that may be a batch file.
/// </para>
///
/// <para>
/// <b>M01 consumes the same event</b>, which is why the inbox id is derived per consumer
/// (<c>SHA-256(messageId:consumerKey)</c>, inside <see cref="IIntegrationInboxGuard"/>) instead of
/// being the bare message id. Two consumers claiming one message under the same primary key would
/// starve each other: whichever module's row landed first would make the other believe the event
/// was already handled, and the losing side would be silently skipped — M01 never activating the
/// client, or this module never onboarding it, depending on the scheduling.
/// </para>
/// </summary>
public sealed class KycValidatedOnboardingConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycValidatedOnboardingConsumer> logger)
    : IConsumer<KycValidatedEvent>
{
    /// <summary>
    /// What distinguishes this consumer's inbox rows from M01's for the same message. Internal so
    /// the idempotency test can name the exact row it expects rather than re-deriving the hash.
    /// </summary>
    internal const string ConsumerKey = nameof(KycValidatedOnboardingConsumer);

    public async Task Consume(ConsumeContext<KycValidatedEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        var ct = context.CancellationToken;

        // The SYSTEM placeholder tenant: there is no tenant to onboard into, and the facade would
        // go looking for its core-banking connection.
        if (evt.TenantId == Guid.Empty) return;

        // The ambient SYSTEM scope must be established BEFORE the DI scope is created:
        // ITenantContext and ICurrentUser are built from it, the facade reads the tenant off
        // ICurrentUser, and a consumer has no HTTP context to fall back on. Hence the explicit
        // scope rather than constructor injection of the services below.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        // First thing that runs, and it commits its own row: a redelivery loses on the inbox
        // primary key and returns, so the customer is queued for creation once. The idempotency
        // key on the command is the second line of defence (INT-05) and would make a duplicate
        // harmless anyway — but it would also make the chain publish a second event per replay.
        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(KycValidatedEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var integration = sp.GetRequiredService<IIntegrationModule>();

        try
        {
            var commandId = await integration.CoreBanking.RequestCustomerCreationAsync(
                evt.CustomerEntityId, ct);

            // The facade deliberately does not save — it enlists in the caller's unit of work —
            // so this consumer IS the caller and must commit. One save for the command row; the
            // inbox row was committed by the guard above.
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "KYC validation of customer {CustomerId} (tenant {TenantId}) queued core-banking "
                + "creation {CommandId}",
                evt.CustomerEntityId, evt.TenantId, commandId.Value);
        }
        catch (DomainException ex)
        {
            // The two refusals the facade raises, and neither is an incident of this consumer:
            // the tenant has no active core-banking connection (an IMF that has not configured
            // one yet — the common case on a fresh deployment), or M01 does not know the customer
            // id in this tenant.
            //
            // Logged as a warning and swallowed. It is a warning rather than information because
            // the inbox row is already committed: this event will not come back, so a tenant that
            // configures its CBS tomorrow has to onboard today's validated customers through the
            // ordinary creation path. The alternative — rethrowing — has the broker redeliver for
            // ever against a condition only an administrator can clear.
            logger.LogWarning(
                "Core-banking onboarding of customer {CustomerId} (tenant {TenantId}) was not "
                + "queued: {Reason}",
                evt.CustomerEntityId, evt.TenantId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else is ours, not the far end's. Swallowed for the same reason: a failure
            // here must not have the broker redeliver a message whose inbox row is committed, and
            // the KYC validation itself stands.
            logger.LogError(ex,
                "Could not queue the core-banking creation of customer {CustomerId} "
                + "(tenant {TenantId})",
                evt.CustomerEntityId, evt.TenantId);
        }
    }
}
