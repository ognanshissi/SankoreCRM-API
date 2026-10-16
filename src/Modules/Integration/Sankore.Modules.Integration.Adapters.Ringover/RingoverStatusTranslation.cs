namespace Sankore.Modules.Integration.Adapters.Ringover;

using System.Collections.Frozen;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The provider's call status, translated — and <b>the rule for a token nobody has seen before</b>
/// (TEL-B-04).
///
/// <para>
/// <b>The table is EMPTY, on purpose, and that is the deliverable.</b> What TEL-B-04 asks for is
/// not a list of tokens — it is a rule: « un statut inconnu est journalisé en alerte et l'appel
/// <b>garde son dernier statut connu</b> ». The rule is implementable, testable and implemented
/// here. The vocabulary is open question 2 and cannot be obtained from anything but a test
/// account, so not one token is guessed.
/// </para>
///
/// <para>
/// <b>Why guessing would be worse than refusing, which is not obvious.</b> A wrong endpoint
/// answers 404 and the failure is seen. A wrong STATUS succeeds: the journal takes it, the call
/// row changes, and the error is invisible for ever. Two readings of it, both real: a conversation
/// reported as missed puts a client on an agent's call-back list and has them rung again by
/// somebody apologising for a call that already happened; a missed call reported as completed
/// takes that client OFF the list, and stops the lead's first-contact clock (TEL-B-12) for a
/// conversation nobody had — so the SLA says the branch responded and the prospect is simply
/// never called. Neither has a symptom anybody reports. That is why the table is empty rather
/// than plausible.
/// </para>
///
/// <para>
/// <b>Keeping the last known status is not a degradation, it is the correct answer.</b> A status
/// this module cannot interpret says nothing about the call, and the journal already has a
/// reading it DOES understand. Writing a default — <c>Failed</c>, say, or <c>Completed</c> —
/// would replace knowledge with a guess, and the four terminal statuses accept nothing further,
/// so that guess would be permanent. <c>TelCall.TryMoveTo</c> is the other half of this rule and
/// already behaves: it answers false rather than throwing, because out-of-order delivery is
/// ordinary.
/// </para>
///
/// <para>
/// <b>Pure.</b> No clock, no I/O, no logger: the alert is the caller's, because only the caller
/// knows which call and which tenant the token arrived for. It is <c>static</c> for the same
/// reason the webhook parser is — a translation that could reach out is a translation an
/// unverified caller can make reach out.
/// </para>
/// </summary>
public static class RingoverStatusTranslation
{
    /// <summary>
    /// Provider token to SANKORE status.
    ///
    /// <para>
    /// <b>Empty until a test account is obtained (open question 2).</b> Adding an entry here is
    /// the whole of the status work and it must be done from OBSERVED traffic, never from a
    /// public page: the point of the account is that the page and the wire disagree often enough
    /// to matter. The comparer is ordinal-ignore-case, so the document's own capitalisation does
    /// not become part of the contract.
    /// </para>
    ///
    /// <para>
    /// Two sub-questions the table alone cannot record, so they are stated: SANKORE separates
    /// <c>Missed</c> from <c>Voicemail</c> because they are different next actions, and separates
    /// <c>Internal</c> from the two real directions because TEL-B-19 excludes it. A provider that
    /// collapses either pair is a provider whose calls this module will bucket more coarsely —
    /// which is a fact to write down when the answers arrive, not a mapping to invent now.
    /// </para>
    /// </summary>
    public static readonly FrozenDictionary<string, TelCallStatus> Known =
        new Dictionary<string, TelCallStatus>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What one provider token means, or that it means nothing yet.
    ///
    /// <para>
    /// Normalises before looking up — trimmed, and matched without regard to case — so a token
    /// that arrives padded or capitalised differently on one event type than another is one token
    /// and not two. Normalisation is NOT interpretation: nothing is stripped, split, or mapped
    /// through a synonym, because every one of those is a guess about a vocabulary nobody has
    /// seen.
    /// </para>
    /// </summary>
    public static RingoverStatusVerdict Translate(string? providerStatus)
    {
        var token = providerStatus?.Trim();

        if (string.IsNullOrEmpty(token))
        {
            // A reading with no status at all. Told apart from an unrecognised one because they
            // are different faults: an absent status is a payload shape this module did not
            // expect, while an unknown one is a vocabulary it has not been given. Both keep the
            // call where it is.
            return new RingoverStatusVerdict(null, string.Empty, Missing: true);
        }

        return Known.TryGetValue(token, out var status)
            ? new RingoverStatusVerdict(status, token, Missing: false)
            : new RingoverStatusVerdict(null, token, Missing: false);
    }
}

/// <summary>
/// The outcome of one translation.
/// </summary>
/// <param name="Status">
/// The status to move the call to, or <c>null</c> for <b>keep the last known status</b>. Null is
/// the answer for both an unknown token and an absent one, and the caller treats them the same
/// way — it only tells them apart in what it logs.
/// </param>
/// <param name="Token">
/// The normalised token, for the alert. Empty when none arrived. It is the one piece of the
/// provider's vocabulary worth putting in a log line: it is what somebody adds to the table.
/// </param>
/// <param name="Missing">
/// True when no status arrived at all, as opposed to one that arrived and is not in the table.
/// </param>
public sealed record RingoverStatusVerdict(TelCallStatus? Status, string Token, bool Missing)
{
    /// <summary>
    /// True when the call must keep the status it already has — which is every case until the
    /// table is filled in.
    ///
    /// <para>
    /// Named rather than left as <c>Status is null</c> at each call site: the rule TEL-B-04 states
    /// is "keeps its last known status", and a reader of the journal should meet that sentence and
    /// not a null check.
    /// </para>
    /// </summary>
    public bool KeepsLastKnownStatus => Status is null;

    /// <summary>
    /// True when this token is worth an ALERT rather than a debug line — the plan says « journalisé
    /// en alerte », and the distinction is which of the two faults it is.
    ///
    /// <para>
    /// An unknown token is an alert: it means the provider reports something the table does not
    /// cover, and somebody has to add it. An ABSENT status is not — a payload carrying no status
    /// is ordinary for event types that are not about a status change at all, and alerting on it
    /// would alert on every one of them.
    /// </para>
    /// </summary>
    public bool DeservesAlert => !Missing && Status is null;
}
