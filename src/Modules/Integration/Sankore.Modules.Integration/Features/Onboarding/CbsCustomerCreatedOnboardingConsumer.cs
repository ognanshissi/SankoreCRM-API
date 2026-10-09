namespace Sankore.Modules.Integration.Features.Onboarding;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Step two of the onboarding chain (INT-14, criterion 2): the customer now exists in the core
/// banking system, so its KYC tier is pushed, and an account is opened IF a product was chosen.
///
/// <para>
/// A separate consumer rather than two more calls inside the creation command's handler, because
/// the creation has to have SUCCEEDED first — the CBS assigns the customer reference, and both
/// follow-up writes address the customer by it. <c>ExecuteIntegrationCommandHandler</c> publishes
/// <see cref="CbsCustomerCreatedEvent"/> through the outbox in the same save as the reference row,
/// so this consumer cannot run before the reference exists.
/// </para>
///
/// <para>
/// <b>The account half is conditional and today the condition is never met.</b>
/// <see cref="IOnboardingProductSelector"/> carries the question and explains why no contract of
/// this platform can answer it yet; the chain therefore stops after the KYC level rather than
/// opening an account on a product code nobody chose.
/// </para>
/// </summary>
public sealed class CbsCustomerCreatedOnboardingConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<CbsCustomerCreatedOnboardingConsumer> logger)
    : IConsumer<CbsCustomerCreatedEvent>
{
    /// <summary>
    /// Per-consumer inbox discriminator, for the same reason as every other consumer of this
    /// module: nothing guarantees this module stays the only consumer of its own events, and the
    /// guard's contract takes the key.
    /// </summary>
    internal const string ConsumerKey = nameof(CbsCustomerCreatedOnboardingConsumer);

    public async Task Consume(ConsumeContext<CbsCustomerCreatedEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // Before the DI scope, like every consumer of this module: ITenantContext and ICurrentUser
        // are built from the ambient background scope, and the facade reads the tenant off the
        // latter.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(CbsCustomerCreatedEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var integration = sp.GetRequiredService<IIntegrationModule>();

        try
        {
            // The tier as M02 computes it. Read through the payload source rather than mapped
            // again here: the "Full / else Simplified / no file at all means None" rule already
            // exists once in this module, and two copies of a compliance tier rule is exactly the
            // kind of drift that shows up as a customer uncapped in the CBS and capped in SANKORE.
            //
            // A null payload means M01 does not know this customer id in this tenant — a dangling
            // reference. Nothing is queued: the CBS already holds a customer we can address, and
            // guessing a tier for a record we cannot read is worse than leaving it as created.
            var payloads = sp.GetRequiredService<CbsCustomerPayloadSource>();
            var payload = await payloads.BuildAsync(evt.TenantId, evt.CrmCustomerId, ct);

            if (payload is null)
            {
                logger.LogWarning(
                    "Customer {CustomerId} was created in the core banking system but is unknown "
                    + "to the clients module in tenant {TenantId}; the onboarding chain stops here",
                    evt.CrmCustomerId, evt.TenantId);
                return;
            }

            var kycCommandId = await integration.CoreBanking.RequestKycLevelUpdateAsync(
                evt.CrmCustomerId, payload.KycLevel, ct);

            var productCode = await sp.GetRequiredService<IOnboardingProductSelector>()
                .SelectAsync(evt.TenantId, evt.CrmCustomerId, ct);

            if (productCode is null)
            {
                // The ordinary path today, and not a failure: the customer is onboarded with its
                // tier and no account. Information rather than a warning — there is nothing for
                // anyone to act on.
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Customer {CustomerId} (tenant {TenantId}) created as {ExternalCustomerId}; "
                    + "KYC level queued as {CommandId}, no product chosen so no account requested",
                    evt.CrmCustomerId, evt.TenantId, evt.ExternalCustomerId, kycCommandId.Value);

                return;
            }

            var accountCommandId = await integration.CoreBanking.RequestAccountOpeningAsync(
                evt.CrmCustomerId, productCode, ct);

            // ONE save for both commands: either the chain's whole second step is queued or none
            // of it is. The two rows are created in this order and carry the clock's reading at
            // creation, which is what the dispatcher's per-customer ordering sorts on
            // (created_at, then id) — so the tier reaches the CBS before the account on any
            // installation whose clock has sub-millisecond resolution, and neither write depends
            // on the other in any case: both address a customer that already exists.
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Customer {CustomerId} (tenant {TenantId}) created as {ExternalCustomerId}; "
                + "KYC level queued as {KycCommandId} and account on {ProductCode} as "
                + "{AccountCommandId}",
                evt.CrmCustomerId, evt.TenantId, evt.ExternalCustomerId,
                kycCommandId.Value, productCode, accountCommandId.Value);
        }
        catch (DomainException ex)
        {
            // A connection deactivated between the creation and this event, or a product code the
            // facade refuses. Swallowed like step one's: the inbox row is committed, so rethrowing
            // would only have the broker redeliver against a condition an administrator clears.
            logger.LogWarning(
                "The onboarding chain of customer {CustomerId} (tenant {TenantId}) stopped after "
                + "the creation: {Reason}",
                evt.CrmCustomerId, evt.TenantId, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not chain the onboarding of customer {CustomerId} (tenant {TenantId}) "
                + "after its core-banking creation",
                evt.CrmCustomerId, evt.TenantId);
        }
    }
}
