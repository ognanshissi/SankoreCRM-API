namespace Sankore.Modules.Integration.Adapters.Dialog360;

/// <summary>
/// What this adapter assumes about 360dialog and has not yet observed on a live account.
///
/// <para>
/// Unlike the Ringover adapter, which waits on a test account before it can do anything, this one
/// is built against a DOCUMENTED contract — Meta's Cloud API, which 360dialog proxies — and works.
/// The questions below are the places where 360dialog's own layer is involved and the published
/// pages leave room for doubt. Each one names its fallback, so the list reads as « what to check
/// first with the sandbox », not « what does not work ».
/// </para>
/// </summary>
public static class Dialog360Specification
{
    public static readonly IReadOnlyList<string> OpenQuestions =
    [
        "1. Webhook authentication. 360dialog does not sign webhook bodies with a secret the "
        + "institution holds (Meta's X-Hub-Signature-256 is keyed with the partner's app secret). "
        + "The endpoint therefore authenticates a shared token sent as the X-Sankore-Webhook-Token "
        + "header, which POST /v1/configs/webhook is asked to add to every delivery. To verify: "
        + "that the custom header is present on EVERY delivery, statuses included. Fallback if it "
        + "is not: put the token in the URL path, as the Brevo SMS callback does.",

        "2. Template catalogue paging. GET /v1/configs/templates is read with limit and offset; to "
        + "verify that `total` is returned so the loop can stop early. Fallback: the loop stops on "
        + "a short page and is bounded at ten pages.",

        "3. Media download host. The media metadata answers a lookaside.fbsbx.com URL; 360dialog "
        + "documents that the same path is served by its own host with the D360-API-KEY header. To "
        + "verify on a real image and a real PDF.",

        "4. Error bodies. Meta's error object is expected inside 360dialog's answers on a refusal; "
        + "360dialog's own `meta` block covers its proxy failures. Unknown codes fall back to the "
        + "HTTP status, never to a retry on a 4xx.",

        "5. Media deletion. WA-BE-21 deletes media at retention with the Cloud API's DELETE "
        + "/{media-id}, sent through 360dialog's host. Whether the proxy forwards it, and whether "
        + "an INBOUND media id can be deleted at all (Meta documents deletion of uploaded media; "
        + "inbound media expire after thirty days), is to verify on a sandbox. The retention never "
        + "depends on it: our own encrypted copy is deleted whatever the provider answers.",
    ];
}
