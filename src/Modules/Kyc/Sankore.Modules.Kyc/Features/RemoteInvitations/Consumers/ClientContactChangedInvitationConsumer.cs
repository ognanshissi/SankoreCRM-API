namespace Sankore.Modules.Kyc.Features.RemoteInvitations.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Withdraws a customer's collection link when M01 reports that the two things the link depends on
/// have changed: their email address, or their date of birth (KRM-B-04).
///
/// <para>
/// <b>Why those two and nothing else.</b> A link is a credential posted to an address, checked
/// against a date. Change the address and the credential is sitting in a mailbox the customer no
/// longer reads — possibly somebody else's. Change the date and the identification question no
/// longer has the answer the customer was told to give: they would try their real date, fail five
/// times, and lock themselves out of their own enrolment. Every other sensitive field — a corrected
/// surname, a new document number, a postal address — leaves the link perfectly valid, and
/// revoking on those would make a routine correction cost the customer a new email and another trip
/// through the flow.
/// </para>
///
/// <para>
/// <b>Idempotence is business-level</b>, as in this module's other consumers: there is no inbox
/// table here (<c>IInboxGuard</c> belongs to M01). A redelivered event finds the link already
/// revoked and <c>RevokeActiveAsync</c> answers false — the aggregate's own revocation is a no-op
/// on an inactive link, so a replay cannot overwrite the first motive.
/// </para>
/// </summary>
// Public because MassTransit instantiates it.
public sealed class ClientContactChangedInvitationConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientContactChangedInvitationConsumer> logger)
    : IConsumer<ClientSensitiveDataChangedEvent>
{
    /// <summary>
    /// The field names M01 puts in <c>ChangedFields</c> for the two values a link depends on.
    /// Strings because the event carries names only — M01's <c>SensitiveField</c> enum is internal
    /// to that module, and this is the price of the boundary.
    /// </summary>
    private const string EmailField = "Email";
    private const string DateOfBirthField = "DateOfBirth";

    public async Task Consume(ConsumeContext<ClientSensitiveDataChangedEvent> context)
    {
        var evt = context.Message;

        if (evt.TenantId == Guid.Empty) return;

        var emailChanged = evt.ChangedFields.Contains(EmailField, StringComparer.OrdinalIgnoreCase);
        var birthDateChanged = evt.ChangedFields.Contains(
            DateOfBirthField, StringComparer.OrdinalIgnoreCase);

        if (!emailChanged && !birthDateChanged) return;

        // Set BEFORE the DI scope: ITenantContext is built from it and a consumer has no HTTP
        // request. The ACTOR is the agent who made the change, not SYSTEM — the revocation is a
        // consequence of their edit and the audit trail should say whose.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.ActorUserId, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var revoker = scope.ServiceProvider.GetRequiredService<KycRemoteInvitationRevoker>();

        try
        {
            // Which file, if any. A customer with no open file has no link, which is the common
            // case for this event.
            var fileIds = await db.KycFiles
                .IgnoreQueryFilters()
                .Where(f => f.TenantId == evt.TenantId && f.CustomerId == evt.ClientId)
                .Select(f => f.Id)
                .ToListAsync(context.CancellationToken);

            if (fileIds.Count == 0) return;

            // The email reason takes precedence when both changed: it is the one that says the
            // credential left the building, which is what an agent needs to read first.
            var reason = emailChanged
                ? KycRemoteInvitationRevoker.EmailChangedReason
                : KycRemoteInvitationRevoker.BirthDateChangedReason;

            var revokedAny = false;

            foreach (var fileId in fileIds)
            {
                revokedAny |= await revoker.RevokeActiveAsync(
                    evt.TenantId, fileId, reason, evt.ActorUserId, context.CancellationToken);
            }

            if (revokedAny)
                await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed and logged. Throwing would hand the message back to the broker and it
            // would fail the same way on every redelivery; meanwhile the link is still checked
            // against the file at use time, so the window this leaves is a stale status on a
            // screen rather than a usable credential. A cancellation is the host shutting down
            // and keeps propagating.
            logger.LogWarning(
                ex, "Revoking the collection links of client {ClientId} threw (tenant {TenantId}).",
                evt.ClientId, evt.TenantId);
        }
    }
}
