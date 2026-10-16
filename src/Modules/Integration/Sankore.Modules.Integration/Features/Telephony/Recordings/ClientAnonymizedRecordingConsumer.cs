namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Destroys a client's recordings when M01 anonymises them (TEL-B-17).
///
/// <para>
/// <b>The FIRST consumer <c>ClientAnonymizedEvent</c> has ever had.</b> M01 has published it since
/// its retention work and nothing listened, which was correct while nothing else held a client's
/// personal data outside the customers schema. A recording of somebody's voice is the clearest
/// possible counter-example: M01 blanking its own columns while this module keeps an audio file of
/// the same person talking would make the anonymisation a statement about one schema rather than
/// about a person.
/// </para>
///
/// <para>
/// <b>It ignores the legal hold, and that is the one decision here worth arguing about.</b> A hold
/// stops the retention PURGE — a routine expiry that a live dispute should not destroy evidence
/// for — but an anonymisation is a different instruction: a right-to-erasure request, or a
/// retention rule the institution has already decided applies. Honouring a hold against it would
/// let a hold placed years ago quietly veto an erasure nobody revisits. If a dispute genuinely
/// requires the audio, the anonymisation of the client is the decision to re-examine, not this
/// consumer.
/// </para>
///
/// <para>
/// <b>And the calls themselves are anonymised, not deleted.</b> <c>TelCall.Anonymise</c> blanks
/// the numbers and the note and keeps the direction, the durations and the outcome — because
/// destroying those would turn a retention rule into a reporting outage, which is the same
/// reasoning M13's lead anonymisation states.
/// </para>
/// </summary>
public sealed class ClientAnonymizedRecordingConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientAnonymizedRecordingConsumer> logger)
    : IConsumer<ClientAnonymizedEvent>
{
    public async Task Consume(ConsumeContext<ClientAnonymizedEvent> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var evt = context.Message;

        if (evt.TenantId == Guid.Empty || evt.ClientId == Guid.Empty) return;

        // Set BEFORE the scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a consumer has no HTTP context.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;

        await ApplyAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetService<IRecordingStore>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            evt.TenantId,
            evt.ClientId,
            CancellationToken.None);
    }

    /// <summary>
    /// Idempotent by construction: a recording already purged answers false from
    /// <c>Purge</c> and a call already anonymised has nothing left to blank, so a redelivered
    /// message does no second piece of work and needs no inbox table.
    /// </summary>
    internal static async Task<int> ApplyAsync(
        IntegrationDbContext db,
        IRecordingStore? store,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid clientId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var now = clock.GetUtcNow();

        var calls = await db.TelCalls
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId
                     && c.EntityType == TelEntityTypes.Customer
                     && c.EntityId == clientId)
            .ToListAsync(ct);

        if (calls.Count == 0) return 0;

        var callIds = calls.Select(c => c.Id).ToList();

        var recordings = await db.TelRecordings
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(r => r.TenantId == tenantId
                     && callIds.Contains(r.CallId)
                     && r.Status != TelRecordingStatus.Purged)
            .ToListAsync(ct);

        var purged = 0;

        foreach (var recording in recordings)
        {
            if (recording.StorageRef is { } storageRef && store is not null)
            {
                try
                {
                    // Bytes first, row second — the ordering the purge job states: a row marked
                    // purged whose object survived is a file nobody will look for again.
                    await store.DeleteAsync(tenantId, storageRef, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Left for the purge job, which will reach it at retention — and the row is
                    // untouched so it stays visible as something still held.
                    logger.LogError(
                        ex, "Could not delete the object of recording {RecordingId} while "
                          + "anonymising client {ClientId} of tenant {TenantId}.",
                        recording.Id, clientId, tenantId);

                    continue;
                }
            }

            // Deliberately NOT LiftLegalHold first: Purge refuses a held recording, and this path
            // bypasses the hold on purpose — see the type's remarks.
            recording.PurgeForAnonymisation(now);
            purged++;
        }

        foreach (var call in calls)
        {
            call.Anonymise();

            if (call.RecordingStatus is TelRecordingStatus.Available
                or TelRecordingStatus.Pending)
            {
                call.SetRecordingStatus(TelRecordingStatus.Purged);
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Client {ClientId} of tenant {TenantId} anonymised: {CallCount} call(s) blanked, "
            + "{Purged} recording(s) destroyed.", clientId, tenantId, calls.Count, purged);

        return purged;
    }
}
