namespace Sankore.Modules.Integration.Features.Insurance.Premiums;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// A premium instalment was collected (ASS-08).
/// </summary>
// Public because MassTransit instantiates it.
public sealed class PremiumPaidConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<PremiumPaidConsumer> logger)
    : IConsumer<IntegrationCommandSucceededEvent>
{
    internal const string ConsumerKey = nameof(PremiumPaidConsumer);

    public async Task Consume(ConsumeContext<IntegrationCommandSucceededEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        // Cheap discriminator before the inbox row: every command of the platform publishes this
        // event, and only a debit can be an instalment's.
        if (evt.CommandType != nameof(CommandType.DebitAccount)) return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandSucceededEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();

        try
        {
            var instalment = await db.PremiumInstalments
                .AsTracking()
                .FirstOrDefaultAsync(i => i.DebitCommandId == evt.CommandId, ct);

            if (instalment is null)
            {
                // A subscription's premium debit, or another chain's. The insurance SAGA owns
                // those, and this consumer leaves them alone — the two are told apart by which
                // table stores the command id, not by the event.
                await db.SaveChangesAsync(ct);
                return;
            }

            // ExternalRef is the CBS's reference for the debit, which ASS-10's statement prints
            // to justify the premium. Nullable: a core banking system may acknowledge without
            // issuing one, and the instalment is paid either way — unlike a SUBSCRIPTION's debit,
            // where a missing reference stops the chain because a reversal could not be keyed on
            // it. Nothing reverses an instalment.
            instalment.MarkPaid(evt.ExternalRef, clock);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Premium instalment {InstalmentId} paid, reference {Reference}",
                instalment.Id, evt.ExternalRef ?? "(none)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not record the payment of the instalment behind command {CommandId}",
                evt.CommandId);
        }
    }
}

/// <summary>
/// A premium instalment's debit was refused — where the product's retry budget is spent (ASS-08,
/// criterion 2).
///
/// <para>
/// <b>The limit is read from the PRODUCT here</b>, at the moment the decision is taken, and never
/// copied onto the instalment. ASS-08 asks for « une politique paramétrable par produit », and an
/// administrator raising the limit means it for the instalments that are already failing — not
/// only for the ones that start failing afterwards.
/// </para>
/// </summary>
public sealed class PremiumFailedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<PremiumFailedConsumer> logger)
    : IConsumer<IntegrationCommandRejectedEvent>
{
    internal const string ConsumerKey = nameof(PremiumFailedConsumer);

    /// <summary>
    /// Used when the policy names no catalogue entry — a synchronised policy whose insurer
    /// product code has no mapping yet. One retry rather than none: the missing mapping is a
    /// configuration gap on THIS side, and a transient refusal must not become an <i>impayé</i>
    /// declared to the insurer on the first attempt because of it.
    /// </summary>
    internal const int FallbackRetryLimit = 1;

    /// <summary>Days between retries when no product can be read.</summary>
    internal const int FallbackRetryIntervalDays = 3;

    public async Task Consume(ConsumeContext<IntegrationCommandRejectedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;
        if (evt.CommandType != nameof(CommandType.DebitAccount)) return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var inbox = sp.GetRequiredService<IIntegrationInboxGuard>();

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId,
                evt.TenantId,
                nameof(IntegrationCommandRejectedEvent),
                ConsumerKey,
                ct))
            return;

        var db = sp.GetRequiredService<IntegrationDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();

        try
        {
            var instalment = await db.PremiumInstalments
                .AsTracking()
                .FirstOrDefaultAsync(i => i.DebitCommandId == evt.CommandId, ct);

            if (instalment is null)
            {
                // A subscription's debit. The saga's own consumer handles it.
                await db.SaveChangesAsync(ct);
                return;
            }

            var (limit, intervalDays) = await ReadRetryPolicyAsync(db, instalment.PolicyId, ct);

            // STRICTLY greater, and the boundary is load-bearing. PremiumRetryLimit counts
            // RETRIES, not attempts — « combien de fois une échéance est relancée avant que
            // l'impayé soit déclaré » — and its validator already documents 0 as "no retry at
            // all". With >= a limit of 3 would spend only two retries, and the ceiling an
            // administrator typed would never be the one applied.
            if (instalment.Attempts > limit)
            {
                // The budget is spent. DeclareUnpaid takes the command id of the declaration TO
                // the insurer, which this platform does not send yet — null says "declared
                // locally, not transmitted", which is honest and is what a later wave fills in.
                instalment.DeclareUnpaid(null, evt.ErrorCode, evt.Detail, clock);

                logger.LogWarning(
                    "Premium instalment {InstalmentId} declared UNPAID after {Attempts} attempt(s) "
                    + "(limit {Limit}): {Family}/{Code}",
                    instalment.Id, instalment.Attempts, limit, evt.ErrorFamily, evt.ErrorCode);
            }
            else
            {
                var nextAttempt = clock.GetUtcNow().AddDays(intervalDays);

                instalment.MarkFailed(evt.ErrorCode, evt.Detail, nextAttempt, clock);

                logger.LogInformation(
                    "Premium instalment {InstalmentId} failed ({Code}), attempt {Attempts} of "
                    + "{Limit}; next attempt {NextAttempt:yyyy-MM-dd}",
                    instalment.Id, evt.ErrorCode, instalment.Attempts, limit, nextAttempt);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not record the failure of the instalment behind command {CommandId}",
                evt.CommandId);
        }
    }

    /// <summary>
    /// The product's retry policy, through the policy that owes the instalment. Falls back when
    /// the policy names no catalogue entry — a synchronised policy whose insurer product code has
    /// no mapping — because refusing to decide would leave the instalment in <c>Debiting</c> for
    /// ever, which no job picks up.
    /// </summary>
    private static async Task<(int Limit, int IntervalDays)> ReadRetryPolicyAsync(
        IntegrationDbContext db, Guid policyId, CancellationToken ct)
    {
        var policy = await db.Policies
            .Where(p => p.Id == policyId)
            .Select(p => p.ProductId)
            .FirstOrDefaultAsync(ct);

        if (policy is not { } productId)
            return (FallbackRetryLimit, FallbackRetryIntervalDays);

        var product = await db.InsuranceProducts
            .Where(p => p.Id == productId)
            .Select(p => new { p.PremiumRetryLimit, p.PremiumRetryIntervalDays })
            .FirstOrDefaultAsync(ct);

        return product is null
            ? (FallbackRetryLimit, FallbackRetryIntervalDays)
            : (product.PremiumRetryLimit, product.PremiumRetryIntervalDays);
    }
}
