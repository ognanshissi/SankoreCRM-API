namespace Sankore.Modules.Integration.Ports;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a conversational messaging provider can be asked to do (WA-BE-01) — the
/// <c>IConversationalChannel</c> of the specification, named like this module's other ports.
///
/// <para>
/// <b>Every member takes its connection</b>, where the telephony port binds "the tenant's
/// connection" once per scope. The difference is deliberate: a tenant may hold one WhatsApp
/// number per agency, so "the tenant's connection" is not one row, and an adapter that picked
/// one would answer a branch's customer from another branch's number. The connection also
/// carries everything an adapter needs — its settings, and the ids its vault keys are built from.
/// </para>
///
/// <para>
/// <b>Numbers cross this port in clear</b>, in E.164 with the plus. They leave M13 encrypted, are
/// decrypted for the one call that needs them, and are never logged, journalled or returned. An
/// adapter that needs another spelling (Meta's API wants digits only) converts them itself.
/// </para>
///
/// <para>
/// <b>Nothing here throws</b>, except <see cref="OperationCanceledException"/> for the caller's
/// own cancellation: every failure is an <see cref="IntegrationResult"/> carrying one of the
/// <c>MESSAGING_*</c> codes, so M13 decides what an agent is told without reading a provider's
/// error number.
/// </para>
/// </summary>
public interface IConversationalChannelPort
{
    /// <summary>
    /// Sends an approved template. Answers the provider's message id (Meta's <c>wamid</c>), which
    /// every later status refers to.
    /// </summary>
    Task<IntegrationResult<string>> SendTemplateAsync(
        IntegrationConnection connection, OutboundTemplate template, CancellationToken ct);

    /// <summary>
    /// Sends free text. Only valid inside the 24-hour window, which the PROVIDER enforces — a
    /// refusal comes back as <see cref="IntegrationErrors.MessagingOutsideWindow"/>.
    /// </summary>
    Task<IntegrationResult<string>> SendTextAsync(
        IntegrationConnection connection, string toE164, string text, CancellationToken ct);

    /// <summary>
    /// Uploads an image or a document and sends it. Same window rule as text. Answers the message
    /// id AND the uploaded media's id, which is how the thread shows the file again later.
    /// </summary>
    Task<IntegrationResult<SentMedia>> SendMediaAsync(
        IntegrationConnection connection, string toE164, OutboundMedia media, CancellationToken ct);

    /// <summary>
    /// The account's templates, whatever their status — the catalogue M13 synchronises and
    /// filters. The provider is the authority on approval; M13 never decides it.
    /// </summary>
    Task<IntegrationResult<IReadOnlyList<ProviderTemplate>>> ListTemplatesAsync(
        IntegrationConnection connection, CancellationToken ct);

    /// <summary>The bytes of one inbound media, by the id the webhook named it with.</summary>
    Task<IntegrationResult<ProviderMedia>> DownloadMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct);

    /// <summary>
    /// Asks the provider to delete a media (WA-BE-21, at its retention). A media the provider no
    /// longer holds is a success: the goal — no copy left there — is reached either way.
    /// </summary>
    Task<IntegrationResult> DeleteMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct);

    /// <summary>
    /// Points the provider's webhook at <paramref name="url"/>, asking it to send
    /// <paramref name="headers"/> on every delivery — which is how the shared token reaches us
    /// for a provider that does not sign its bodies.
    /// </summary>
    Task<IntegrationResult> ConfigureWebhookAsync(
        IntegrationConnection connection,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct);

    /// <summary>
    /// Reads a webhook body. <b>Pure</b> — no I/O, no clock, no database — and never throws on
    /// a body it does not understand: it answers an empty batch. It runs inside a Hangfire job on
    /// a body that was verified at the door, but the body still came from outside.
    /// </summary>
    ConversationalWebhookBatch ParseWebhook(string body);
}

/// <param name="ToE164">The recipient, with the plus.</param>
/// <param name="Name">The template's name as the provider knows it.</param>
/// <param name="Language">The template's language code, e.g. <c>fr</c> or <c>en_US</c>.</param>
/// <param name="BodyParameters">The values of <c>{{1}}</c>, <c>{{2}}</c>… in order.</param>
public sealed record OutboundTemplate(
    string ToE164,
    string Name,
    string Language,
    IReadOnlyList<string> BodyParameters);

/// <summary>A media sent: the provider's message id and the id of the uploaded file.</summary>
public sealed record SentMedia(string ProviderMessageId, string MediaId);

/// <summary>An image or a document to send. Bytes in hand: the upload is the adapter's job.</summary>
public sealed record OutboundMedia(byte[] Content, string ContentType, string FileName, string? Caption);

/// <summary>
/// One template as the provider lists it.
/// </summary>
/// <param name="Status">
/// The provider's word, upper-cased (<c>APPROVED</c>, <c>PENDING</c>, <c>REJECTED</c>,
/// <c>PAUSED</c>, <c>DISABLED</c>…). Translated by M13, which owns the rule that only an approved
/// template may be offered.
/// </param>
/// <param name="Buttons">The buttons' labels, for the preview. Never sent back.</param>
public sealed record ProviderTemplate(
    string? ProviderTemplateId,
    string Name,
    string Language,
    string Category,
    string Status,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons);

/// <summary>The bytes of one media and what the provider says they are.</summary>
public sealed record ProviderMedia(byte[] Content, string ContentType, string? FileName);

/// <summary>
/// Everything one webhook body said.
/// </summary>
/// <param name="PhoneNumberIds">
/// Every phone-number id the body names. A body routed to a connection must name that
/// connection's id and no other — the job refuses the entries that do not.
/// </param>
public sealed record ConversationalWebhookBatch(
    IReadOnlyList<string> PhoneNumberIds,
    IReadOnlyList<ProviderInboundMessage> Messages,
    IReadOnlyList<ProviderMessageStatus> Statuses)
{
    public static ConversationalWebhookBatch Empty { get; } = new([], [], []);
}

/// <summary>
/// One message a customer sent.
/// </summary>
/// <param name="From">The sender in E.164, with the plus. Personal data — never logged.</param>
/// <param name="ProfileName">The name the sender's WhatsApp profile shows. Not an identity.</param>
/// <param name="Type">
/// Normalised: <c>text</c>, <c>image</c>, <c>document</c>, <c>audio</c>, <c>video</c>,
/// <c>sticker</c>, <c>location</c>, <c>contacts</c>, <c>button</c>, <c>interactive</c>,
/// <c>reaction</c> or <c>unsupported</c>.
/// </param>
/// <param name="Text">
/// What can be shown as text: the body of a text, the caption of a media, the label of a pressed
/// button. Null for a media without caption.
/// </param>
public sealed record ProviderInboundMessage(
    string PhoneNumberId,
    string ProviderMessageId,
    string From,
    string? ProfileName,
    DateTimeOffset Timestamp,
    string Type,
    string? Text,
    string? MediaId,
    string? MediaMimeType,
    string? MediaFileName,
    string? ReplyToProviderMessageId);

/// <summary>
/// One delivery status of a message we sent.
/// </summary>
/// <param name="Status">Lower-case, as Meta spells it: <c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c>.</param>
public sealed record ProviderMessageStatus(
    string PhoneNumberId,
    string ProviderMessageId,
    string Status,
    DateTimeOffset Timestamp,
    string? ErrorCode,
    string? ErrorTitle);
