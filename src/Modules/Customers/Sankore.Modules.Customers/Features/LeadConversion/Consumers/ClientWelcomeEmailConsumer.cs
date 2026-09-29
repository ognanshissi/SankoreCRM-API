namespace Sankore.Modules.Customers.Features.LeadConversion.Consumers;

using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Welcomes a prospect who has just become a client.
///
/// Reacting to <see cref="ClientCreatedEvent"/> rather than sending from the conversion handler
/// in M13 is deliberate on three counts: the client record is the source of truth for the
/// address (an agent may have corrected what the lead carried), the fact being announced is
/// "you are now a client" which belongs to M01, and a mail server being slow or down cannot
/// then slow down or fail the conversion itself.
///
/// Only conversions are welcomed: a client keyed in directly at a counter is standing in front
/// of an agent, and a "welcome" mail for a relationship that started ten minutes ago in person
/// reads as spam. <see cref="ClientCreatedEvent.SourceLeadId"/> is exactly that distinction.
/// </summary>
public sealed class ClientWelcomeEmailConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    IFieldEncryptor encryptor,
    INotificationsModule notifications,
    ITenantStore tenantStore,
    ILogger<ClientWelcomeEmailConsumer> logger)
    : IConsumer<ClientCreatedEvent>
{
    public const string TemplateKey = "client.welcome";

    private const string ConsumerKey = nameof(ClientWelcomeEmailConsumer);

    public async Task Consume(ConsumeContext<ClientCreatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.SourceLeadId is null)
            return;

        var messageId = context.MessageId ?? evt.EventId;

        // At-least-once delivery: a redelivered event loses on the inbox primary key, so the
        // client is welcomed once and not once per retry.
        if (!await inbox.TryBeginAsync(
                DeriveInboxId(messageId), evt.TenantId, $"{nameof(ClientCreatedEvent)}:{ConsumerKey}", ct))
            return;

        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == evt.TenantId && c.Id == evt.ClientId)
            .Select(c => new { c.DisplayName, c.ClientNumber, c.PreferredLanguage })
            .FirstOrDefaultAsync(ct);

        if (client is null)
        {
            logger.LogWarning(
                "Client {ClientId} disappeared before it could be welcomed; nothing sent.",
                evt.ClientId);
            await db.SaveChangesAsync(ct);
            return;
        }

        var email = await ResolveEmailAsync(evt.TenantId, evt.ClientId, ct);
        if (email is null)
        {
            // Perfectly ordinary: a lead captured by an agent in the field often has a phone
            // number and nothing else. The inbox row is still committed so a redelivery does
            // not re-run this lookup forever.
            logger.LogInformation(
                "Client {ClientId} has no email address; welcome message skipped.", evt.ClientId);
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var tenantInfo = await tenantStore.GetAsync(evt.TenantId, ct);
            var locale = client.PreferredLanguage ?? tenantInfo?.DefaultLanguage ?? "fr";

            await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: TemplateKey,
                RecipientEmail: email,
                RecipientName: client.DisplayName,
                Module: "Customers",
                Locale: locale,
                TemplateData: new Dictionary<string, object>
                {
                    ["full_name"] = client.DisplayName,
                    // The client number is what the client is asked for at a counter or on the
                    // phone; it is the one piece of the record worth putting in the message.
                    ["client_number"] = client.ClientNumber,
                    ["company_name"] = tenantInfo?.Name ?? string.Empty,
                },
                // One welcome per client, for good: not keyed on the message, so even a
                // re-created inbox table cannot produce a second one.
                IdempotencyKey: $"{TemplateKey}-{evt.ClientId}",
                TenantId: evt.TenantId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client exists and the conversion stands; a failed welcome must not roll the
            // inbox row back and have the broker redeliver forever.
            logger.LogError(ex,
                "Could not queue the welcome message for client {ClientId}.", evt.ClientId);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads the client's primary email, preferring the one marked primary and falling back to
    /// any active one. This is the single place in this flow that decrypts anything, and it
    /// decrypts exactly one value.
    /// </summary>
    private async Task<string?> ResolveEmailAsync(Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var encrypted = await db.ClientContactPoints
            .IgnoreQueryFilters()
            .Where(cp => cp.TenantId == tenantId
                      && cp.ClientId == clientId
                      && cp.Type == ContactPointType.Email
                      && cp.ValidTo == null)
            .OrderByDescending(cp => cp.IsPrimary)
            .Select(cp => cp.EncryptedValue)
            .FirstOrDefaultAsync(ct);

        if (encrypted is null)
            return null;

        try
        {
            return encryptor.Decrypt(encrypted);
        }
        catch (Exception ex)
        {
            // A value that cannot be decrypted is a key problem, not a reason to crash a
            // welcome message. It is worth shouting about, though.
            logger.LogError(ex,
                "Could not decrypt the email of client {ClientId}; welcome message skipped.", clientId);
            return null;
        }
    }

    /// <summary>
    /// Deterministic per-consumer inbox id, same device as the other consumers of this event:
    /// two consumers of one message must not compete for the same inbox primary key.
    /// </summary>
    private static Guid DeriveInboxId(Guid messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ConsumerKey}:{messageId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
