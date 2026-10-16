namespace Sankore.Modules.Integration.Features.Telephony.Stats;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Refreshes the day an agent just qualified a call in (TEL-B-19).
///
/// <para>
/// <b>This is what "fed by events" means here, and it is deliberately not an increment.</b> The
/// event says which day changed; the recomputation then counts that day from the journal. So a
/// redelivered message, two agents wrapping up at once, or a call qualified twice all produce the
/// same figures — there is no arithmetic to get wrong, and no inbox table needed to make the
/// consumer idempotent.
/// </para>
///
/// <para>
/// <b>Why the wrap-up and not the journal.</b> Qualification is the figure an agent watches move:
/// they record an outcome and look at their own counter. A missed call, by contrast, is a figure
/// nobody refreshes a screen for — the hourly job covers it within the hour, which is why the
/// journal does not pay for a recomputation on every webhook of every call.
/// </para>
/// </summary>
public sealed class TelephonyStatsConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<TelephonyStatsConsumer> logger)
    : IConsumer<TelephonyCallWrappedUpEvent>
{
    public async Task Consume(ConsumeContext<TelephonyCallWrappedUpEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;

        if (evt.TenantId == Guid.Empty) return;

        // Set BEFORE the scope is created: a consumer has no HTTP context, and ITenantContext is
        // built from this ambient one when the scope resolves it.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;

        // The day of the CALL, not of the wrap-up. An agent qualifying yesterday's missed call
        // this morning must move yesterday's figures, which is the whole point of keying the read
        // model on the call's own instant.
        var day = DateOnly.FromDateTime(evt.StartedAt.UtcDateTime);

        await TelCallStatsRecomputer.RecomputeRangeAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            evt.TenantId,
            day,
            day,
            CancellationToken.None);
    }
}
