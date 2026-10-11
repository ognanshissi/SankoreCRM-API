namespace Sankore.Modules.Customers.Features.Household.Consumers;

using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Kernel;

/// <summary>
/// Tells the advisor that a departing household member left beneficiary designations behind
/// (BNF-B-13, BNF-B-15).
///
/// <para>
/// This is the alert BNF-B-13 asks for, and it is worth stating what it is about: a removed
/// member's designations are moved to <c>ToReview</c> — never deleted, never reallocated — which
/// means the rank they belonged to <b>no longer totals 100%</b>. That is precisely the state an
/// agent must be shown, because nothing else will surface it until a claim.
/// </para>
///
/// <para>
/// <c>HouseholdMemberRemovedEvent</c> fires only when something was actually flagged, so there is
/// no "nothing to report" case to filter here.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class HouseholdMemberRemovedAlertConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    IAdministrationModule administration,
    ILogger<HouseholdMemberRemovedAlertConsumer> logger)
    : IConsumer<HouseholdMemberRemovedEvent>
{
    public const string TemplateKey = "customers.household.designations_to_review";

    private const string ConsumerKey = nameof(HouseholdMemberRemovedAlertConsumer);

    public async Task Consume(ConsumeContext<HouseholdMemberRemovedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (evt.TenantId == Guid.Empty) return;

        var messageId = context.MessageId ?? evt.EventId;

        // At-least-once delivery: a redelivered event loses on the inbox primary key.
        if (!await inbox.TryBeginAsync(
                DeriveInboxId(messageId),
                evt.TenantId,
                $"{nameof(HouseholdMemberRemovedEvent)}:{ConsumerKey}",
                ct))
            return;

        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == evt.TenantId && c.Id == evt.ClientId)
            .Select(c => new { c.DisplayName, c.ClientNumber, c.AdvisorUserId })
            .FirstOrDefaultAsync(ct);

        if (client is null)
        {
            logger.LogWarning(
                "Client {ClientId} disappeared before its advisor could be alerted about "
                + "{Count} designation(s) to review",
                evt.ClientId, evt.DesignationsToReview);
            await db.SaveChangesAsync(ct);
            return;
        }

        // The advisor owns the file, so they are the right recipient. Falling back to the agent
        // who removed the member is deliberate and always available: they are holding the file at
        // that moment, and the consequence of the removal is not visible from the screen they
        // were on. A client with no advisor and no alert at all is the one outcome to avoid —
        // nothing else will ever mention the broken rank.
        var recipientId = client.AdvisorUserId ?? evt.RemovedBy;

        if (recipientId == Guid.Empty)
        {
            logger.LogWarning(
                "No advisor and no actor to alert about {Count} designation(s) to review on "
                + "client {ClientId}",
                evt.DesignationsToReview, evt.ClientId);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Addressed through M12, which owns the user's address and locale. Never throws, so the
        // inbox row below is committed whatever the mail server did.
        var queued = await administration.NotifyUserAsync(
            new NotifyUserRequest(
                TenantId: evt.TenantId,
                UserId: recipientId,
                TemplateKey: TemplateKey,
                // Keyed on the removed MEMBER, not on the message: one alert per departure, for
                // good, even if the inbox table were recreated.
                IdempotencyKey: $"{TemplateKey}-{evt.MemberId}",
                TemplateData: new Dictionary<string, object>
                {
                    // The client number is what an agent searches on. The member is identified by
                    // nothing at all: a rendered body reaches a mail provider and sits in the
                    // delivery log, and who is in a client's household is exactly the kind of
                    // value this repo keeps out of both.
                    ["client_number"] = client.ClientNumber,
                    ["client_name"] = client.DisplayName,
                    ["designations_to_review"] = evt.DesignationsToReview,
                    ["holdings_affected"] = evt.AffectedHoldingIds.Count,
                }),
            ct);

        if (!queued)
        {
            logger.LogWarning(
                "Could not alert user {UserId} about {Count} designation(s) to review on "
                + "client {ClientId}",
                recipientId, evt.DesignationsToReview, evt.ClientId);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deterministic per-consumer inbox id, the same device the other consumers of this module
    /// use: two consumers of one message must not compete for the same inbox primary key.
    /// </summary>
    private static Guid DeriveInboxId(Guid messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{ConsumerKey}:{messageId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
