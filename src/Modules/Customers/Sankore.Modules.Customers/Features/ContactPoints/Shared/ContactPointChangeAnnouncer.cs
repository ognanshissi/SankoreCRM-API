namespace Sankore.Modules.Customers.Features.ContactPoints.Shared;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Announces that a client's reachable contact details changed.
///
/// <para>
/// <b>Why these slices publish at all now.</b> Until M02 started sending customers a single-use
/// link by email, nothing outside this module cared which address was on file — the three
/// contact-point slices committed and published nothing. They now have to: the link is a bearer
/// credential posted to an address, so changing that address leaves a live credential in a mailbox
/// the customer no longer reads, possibly somebody else's. M02 withdraws the link on this event
/// (KRM-B-04), and without it the only thing that would eventually close the link is its own
/// expiry, days later.
/// </para>
///
/// <para>
/// <b>It reuses <see cref="ClientSensitiveDataChangedEvent"/> rather than adding an event.</b>
/// That event already means "one or more protected identity fields changed, here are their NAMES"
/// — which is exactly this, and its contract already promises no value ever travels with it. A
/// second, contact-point-shaped event would give consumers two places to learn the same fact.
/// </para>
///
/// <para>
/// Written through the OUTBOX of this module's own DbContext, so the announcement commits with the
/// change. An event without its change would have M02 revoke a link for an address that is still
/// the one on file.
/// </para>
/// </summary>
// Internal would be enough for production — only three handlers in this assembly use it — but the
// test project builds against the public surface for its doubles, as it does for ClientEmailNotifier.
public sealed class ContactPointChangeAnnouncer(
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
{
    /// <summary>
    /// Queues the announcement for one contact-point type. Call it BEFORE
    /// <c>SaveChangesAsync</c> — the outbox row is part of the same transaction.
    /// </summary>
    /// <param name="reason">
    /// What happened, in one short sentence. The event's <c>Reason</c> is an operator-read motive
    /// and these slices take none from the caller, so it is a fixed description of the operation
    /// rather than a blank: "the email changed" with no reason recorded is the kind of audit row
    /// that looks complete and says nothing.
    /// </param>
    public Task AnnounceAsync(
        Guid tenantId,
        Guid clientId,
        ContactPointType type,
        Guid actor,
        string reason,
        CancellationToken ct)
        => publisher.PublishAsync(
            new ClientSensitiveDataChangedEvent(
                TenantId: tenantId,
                ClientId: clientId,
                // The NAME of the field, matching the spelling UpdateClientSensitiveHandler uses
                // (SensitiveField) so a consumer has one vocabulary to match against. No value, in
                // clear or encrypted — a consumer that needs one calls the audited reveal endpoint
                // under its own user's identity.
                ChangedFields: [FieldNameFor(type)],
                Reason: reason,
                ActorUserId: actor),
            ct);

    /// <summary>
    /// The <c>SensitiveField</c> name for a contact-point type. Mapped explicitly rather than by
    /// <c>ToString()</c> on the type: the two enums agree on three members today and nothing keeps
    /// them agreeing, and a consumer matching on "Email" must not stop matching because somebody
    /// renamed a contact-point type.
    /// </summary>
    private static string FieldNameFor(ContactPointType type) => type switch
    {
        ContactPointType.Email => nameof(SensitiveField.Email),
        ContactPointType.Phone => nameof(SensitiveField.Phone),
        ContactPointType.Address => nameof(SensitiveField.PostalAddress),
        _ => type.ToString(),
    };
}
