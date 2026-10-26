using Sankore.Shared.Kernel;

namespace Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Public contract exposed by the Notifications module.
/// Other modules reference only this interface — never the main assembly.
/// </summary>
public interface INotificationsModule
{
    /// <summary>
    /// Enqueues a transactional email for asynchronous delivery.
    /// The caller's transaction must be committed before calling this so that
    /// the email record is persisted atomically with the business operation
    /// (use the Outbox pattern inside the calling handler, or call this after
    /// SaveChangesAsync succeeds in the same unit of work).
    /// Returns the Id of the created EmailOutboxMessage.
    /// </summary>
    Task<Result<Guid>> QueueEmailAsync(QueueEmailRequest request, CancellationToken ct = default);

    /// <summary>
    /// Enqueues a transactional text message for asynchronous delivery. Returns the id of the
    /// created outbox row.
    ///
    /// <para>
    /// The same contract as <see cref="QueueEmailAsync"/> — call it after the business operation
    /// has committed — and the same idempotency: a duplicate key is dropped, which on this channel
    /// is the difference between one charge and two.
    /// </para>
    ///
    /// <para>
    /// <b>Queuing is not sending, and on this channel the gap matters.</b> Whether the tenant HAS
    /// an SMS channel, whether its monthly allowance is exhausted and whether a template exists
    /// are all settled by the outbox processor, which dead-letters with a reason. A success here
    /// means the message is queued and nothing more. A caller that needs to know a message can
    /// actually go out — a second-factor flow, which must not strand a customer at a code screen
    /// — asks <c>IAdministrationModule.GetSmsConfigAsync</c> first and reads <c>CanSend</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Every message sent through this is TRANSACTIONAL.</b> There is no marketing path and no
    /// consent record to support one: the providers are configured in transactional mode, which is
    /// what lets a one-time code arrive at two in the morning. Adding a campaign channel means
    /// adding an opt-in record first, not reusing this one.
    /// </para>
    /// </summary>
    Task<Result<Guid>> QueueSmsAsync(QueueSmsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Puts a notification in a user's bell and nudges their open tabs (WA-FE-10). Idempotent on
    /// <see cref="InAppNotificationRequest.IdempotencyKey"/>: the same key twice notifies once.
    /// Never throws for a delivery problem — the push is best-effort, the row is the record.
    /// </summary>
    Task<Result<Guid>> NotifyInAppAsync(InAppNotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// The e-mails and SMS queued for a client, newest first — for the client record's « Documents »
    /// tab (2026-10-26). Past the technical retention the recipient and the content are gone and the
    /// trace stays (channel, template, dates, status), until the client is anonymised.
    /// </summary>
    Task<IReadOnlyList<ClientMessageSummary>> ListClientMessagesAsync(
        Guid tenantId, Guid clientId, CancellationToken ct = default);
}

/// <param name="Category">Dotted and stable, e.g. <c>whatsapp.escalation</c>.</param>
/// <param name="Title">One line. Shown in the bell and the toast.</param>
/// <param name="Body">One sentence of context. No message content, no number.</param>
/// <param name="Link">A CRM route the notification opens, starting with <c>/</c>.</param>
public sealed record InAppNotificationRequest(
    Guid TenantId,
    Guid UserId,
    string Category,
    string Title,
    string? Body,
    string? Link,
    string IdempotencyKey);

/// <summary>One message a client was sent.</summary>
/// <param name="Channel"><c>Email</c> or <c>Sms</c>.</param>
/// <param name="Status">The outbox status: Pending, Sending, Sent, Failed, DeadLettered.</param>
/// <param name="Recipient">Masked; null once the content has been purged.</param>
public sealed record ClientMessageSummary(
    Guid MessageId,
    string Channel,
    string TemplateKey,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    string? Recipient);
