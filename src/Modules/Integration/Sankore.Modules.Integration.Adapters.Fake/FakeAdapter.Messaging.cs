namespace Sankore.Modules.Integration.Adapters.Fake;

using System.Globalization;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Messaging;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The messaging half of the double (WA-BE-01): a WhatsApp number that accepts every message and
/// reports two approved templates, so the whole chain — consent, template, send, statuses,
/// inbound reply — can be walked in Development with no 360dialog account.
///
/// <para>
/// <b>The one non-deterministic answer of the double is a message id</b>, and on purpose: the
/// double is scoped, so a counter alone would restart at 1 on every request and hand M13 the same
/// <c>wamid</c> twice — which its unique index would refuse, as it should for a real provider. The
/// id is therefore a counter plus a random suffix. Its webhook reader is the REAL one (<see cref="CloudApiWebhook"/>), so the
/// development simulator exercises exactly the code a 360dialog delivery runs through.
/// </para>
/// </summary>
public sealed partial class FakeAdapter
{
    private int _messageSequence;

    /// <summary>What the double was asked to send, newest last, for a test to assert on.</summary>
    public List<OutboundTemplate> SentTemplates { get; } = [];

    /// <summary>The catalogue the double reports. Two French templates, both approved.</summary>
    public List<ProviderTemplate> MessagingTemplates { get; } =
    [
        new ProviderTemplate(
            ProviderTemplateId: "fake-tpl-1",
            Name: "premier_contact",
            Language: "fr",
            Category: "UTILITY",
            Status: "APPROVED",
            HeaderText: null,
            BodyText: "Bonjour {{1}}, je suis {{2}}, votre conseiller. Vous nous avez fait part de "
                      + "votre intérêt pour {{3}}. Quand puis-je vous appeler ?",
            FooterText: "Répondez STOP pour ne plus être contacté.",
            Buttons: ["Aujourd'hui", "Demain"]),
        new ProviderTemplate(
            ProviderTemplateId: "fake-tpl-2",
            Name: "relance_dossier",
            Language: "fr",
            Category: "UTILITY",
            Status: "APPROVED",
            HeaderText: "Votre dossier",
            BodyText: "Bonjour {{1}}, il manque encore quelques éléments à votre dossier. Puis-je "
                      + "vous aider à le compléter ?",
            FooterText: null,
            Buttons: []),
    ];

    public Task<IntegrationResult<string>> SendTemplateAsync(
        IntegrationConnection connection, OutboundTemplate template, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(template);

        // A template the catalogue does not hold in that language is refused the way Meta refuses
        // it, so the failure path of the agent's screen can be walked too.
        var known = MessagingTemplates.Any(t =>
            t.Name == template.Name
            && string.Equals(t.Language, template.Language, StringComparison.OrdinalIgnoreCase)
            && t.Status == "APPROVED");

        return Task.FromResult(Read(
            FakeAdapterOperations.SendTemplate, template.Name,
            () =>
            {
                if (!known)
                {
                    return IntegrationResult.Functional<string>(
                        IntegrationErrors.MessagingTemplateRejected,
                        $"The fake catalogue holds no approved template {template.Name} ({template.Language}).");
                }

                SentTemplates.Add(template);
                return IntegrationResult.Ok(NextMessageId());
            }));
    }

    public Task<IntegrationResult<string>> SendTextAsync(
        IntegrationConnection connection, string toE164, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read(
            FakeAdapterOperations.SendText, target: null, () => IntegrationResult.Ok(NextMessageId())));
    }

    public Task<IntegrationResult<SentMedia>> SendMediaAsync(
        IntegrationConnection connection, string toE164, OutboundMedia media, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read(
            FakeAdapterOperations.SendMedia, media.FileName,
            () => IntegrationResult.Ok(new SentMedia(NextMessageId(), "fake-media-" + Guid.NewGuid().ToString("N")[..12]))));
    }

    public Task<IntegrationResult<IReadOnlyList<ProviderTemplate>>> ListTemplatesAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<ProviderTemplate>>(
            FakeAdapterOperations.ListTemplates, target: null,
            () => IntegrationResult.Ok<IReadOnlyList<ProviderTemplate>>([.. MessagingTemplates])));
    }

    /// <summary>A one-pixel PNG, whatever the id: enough for a preview to render.</summary>
    public Task<IntegrationResult<ProviderMedia>> DownloadMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read(
            FakeAdapterOperations.DownloadMedia, mediaId,
            () => IntegrationResult.Ok(new ProviderMedia(OnePixelPng, "image/png", "image.png"))));
    }

    public Task<IntegrationResult> DeleteMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(VoidRead(FakeAdapterOperations.DeleteMedia, mediaId, IntegrationResult.Ok()));
    }

    public Task<IntegrationResult> ConfigureWebhookAsync(
        IntegrationConnection connection,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VoidRead(FakeAdapterOperations.ConfigureWebhook, url.AbsoluteUri, IntegrationResult.Ok()));
    }

    public ConversationalWebhookBatch ParseWebhook(string body) => CloudApiWebhook.Parse(body);

    private string NextMessageId()
        => "wamid.FAKE" + (++_messageSequence).ToString("D6", CultureInfo.InvariantCulture)
           + Guid.NewGuid().ToString("N")[..8];

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
