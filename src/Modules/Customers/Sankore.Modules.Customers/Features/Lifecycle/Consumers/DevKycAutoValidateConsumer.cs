namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Development-only stand-in for module M02: on client creation it publishes the
/// <see cref="KycValidatedEvent"/> that the real KYC module would emit, so the
/// whole PendingKyc → Active chain can be exercised before M02 exists.
///
/// TWO independent gates, both required:
/// <list type="number">
/// <item><see cref="IHostEnvironment.IsDevelopment"/> — checked FIRST and
///       unconditionally. Outside Development this consumer does nothing at all,
///       whatever the tenant setting says: a production tenant must never be able
///       to switch its own KYC control off through a data value.</item>
/// <item>the tenant setting <c>kyc-stub-enabled</c> — so a shared development
///       environment can keep the stub off for tenants that test the real flow.</item>
/// </list>
/// </summary>
public sealed class DevKycAutoValidateConsumer(
    CustomersDbContext db,
    IHostEnvironment environment,
    ICustomerSettings settings,
    IInboxGuard inbox,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    ILogger<DevKycAutoValidateConsumer> logger)
    : IConsumer<ClientCreatedEvent>
{
    /// <summary>
    /// Discriminator folded into the inbox id. <c>ClientCreatedEvent</c> is consumed
    /// by more than one consumer of this module (the timeline projection is the other
    /// one) and <c>InboxMessage</c>'s primary key IS the event id — so guarding on
    /// the raw event id would let whichever consumer runs first starve the other.
    /// Each consumer therefore claims its own deterministic derivative of the message
    /// id: still exactly one row per (message, consumer), still idempotent on replay.
    /// </summary>
    private const string ConsumerKey = nameof(DevKycAutoValidateConsumer);

    public async Task Consume(ConsumeContext<ClientCreatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        // Gate 1 — environment, before anything else: no inbox row, no setting read,
        // no side effect whatsoever outside Development.
        if (!environment.IsDevelopment())
            return;

        var messageId = context.MessageId ?? evt.EventId;

        // At-least-once delivery: a replay loses on the inbox primary key, so the
        // stub cannot approve the same client twice.
        if (!await inbox.TryBeginAsync(
                DeriveInboxId(messageId), evt.TenantId, $"{nameof(ClientCreatedEvent)}:{ConsumerKey}", ct))
            return;

        // SYSTEM identity: the stub acts for the platform, not for the operator who
        // happened to create the client.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, LifecycleActors.System, "SYSTEM");

        // Gate 2 — tenant setting.
        var stubEnabled = await settings.GetBoolAsync(
            evt.TenantId, CustomerSettingKeys.KycStubEnabled, ct);

        if (!stubEnabled)
            return;

        // Staged in the outbox, committed by the SaveChanges below together with the
        // inbox row: the KYC approval is either announced once or not at all.
        await publisher.PublishAsync(
            new KycValidatedEvent(evt.TenantId, evt.ClientId, DateTimeOffset.UtcNow), ct);

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "KYC stub auto-approved client {ClientId} (tenant {TenantId}). Development only — " +
            "module M02 is not involved.",
            evt.ClientId, evt.TenantId);
    }

    /// <summary>
    /// Deterministic per-consumer inbox id: SHA-256 of "consumer:messageId",
    /// truncated to 16 bytes. Same message + same consumer always yields the same
    /// id (so a replay is still detected), while two consumers of the same message
    /// never collide. Not a security primitive — only a namespacing device.
    /// </summary>
    private static Guid DeriveInboxId(Guid messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ConsumerKey}:{messageId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
