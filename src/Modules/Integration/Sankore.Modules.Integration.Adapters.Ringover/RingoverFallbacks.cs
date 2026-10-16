namespace Sankore.Modules.Integration.Adapters.Ringover;

/// <summary>
/// The spec's fallbacks, which <b>apply by default and are already built</b> (TEL-B-04).
///
/// <para>
/// Three of the six open points have a stated fallback, and the plan is explicit that « les replis
/// de la spec s'appliquent par défaut et le lot les révise ». This type records which behaviour
/// each one is and <b>where it already lives</b>, because that is the fact a reader of this
/// adapter most needs and least expects: the fallbacks are not stubs waiting here for the
/// provider's answer — they are the production behaviour of lots 2, 3 and 5, written and tested
/// against the in-memory adapter.
/// </para>
///
/// <para>
/// Which changes what an answer means. None of the three can unblock anything, because nothing is
/// blocked on them: each answer can only replace a working mechanism with a cheaper or more exact
/// one. That is worth stating plainly in one place, because the alternative reading — "six open
/// points, so six things do not work" — is wrong and would have somebody rebuild what exists.
/// </para>
///
/// <para>
/// A type and not a comment for the same reason <see cref="RingoverSpecification.OpenQuestions"/>
/// is a list: a test asserts that the three are described here, so a future change that quietly
/// removes one of the fallbacks has somewhere to fail.
/// </para>
/// </summary>
public static class RingoverFallbacks
{
    /// <summary>
    /// Open point 4 — webhook authenticity.
    /// </summary>
    public const string WebhookAuthenticity =
        "A per-connection URL token, plus a re-read of the call through the API: nothing in a "
        + "webhook body is ever trusted, whatever it says. Implemented by SyncWebhookVerifier "
        + "(HMAC over timestamp and body, replay window, constant-time comparison) and by "
        + "tel_webhook_event, which makes a redelivery idempotent — the gap the verifier's own "
        + "remarks identified. A provider signature would let a forged body be rejected before "
        + "the re-read; it would not make the body trustworthy, because it is already not "
        + "trusted.";

    /// <summary>
    /// Open point 5 — correlating a click-to-call with the call the provider places.
    /// </summary>
    public const string ClickToCallCorrelation =
        "By the provider's own reference first, and by (agent, number, two-minute window) when "
        + "there is none. Implemented in TelCallJournalWriter, where ambiguity is measured in "
        + "AGENTS rather than in requests: two colleagues ringing the same number inside the "
        + "window attach to neither, because crediting one with the other's conversation is worse "
        + "than leaving the call unattributed, while two requests from ONE agent is a double-click "
        + "and the later one wins. A provider reference would make it exact and retire the window.";

    /// <summary>
    /// Open point 6 — destroying the provider's copy of a recording.
    /// </summary>
    public const string RecordingDeletion =
        "Purge on the SANKORE side only. The RecordingDeletion capability is simply not declared, "
        + "so PurgeTelephonyRecordingsJob never asks the provider and no call fails for want of "
        + "it. The consequence is stated rather than discovered: a recording destroyed here still "
        + "exists at the provider, so the retention is a statement about SANKORE and not about "
        + "the conversation — which is a sentence that belongs in whatever the institution tells "
        + "a regulator, and the reason this point is asked at all.";

    /// <summary>
    /// The three, in the order of the open questions they answer. Exposed so a test can assert
    /// that each one is still described, and an operator screen can print them next to the
    /// questions.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        WebhookAuthenticity,
        ClickToCallCorrelation,
        RecordingDeletion,
    ];
}
