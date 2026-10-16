namespace Sankore.Modules.Integration.Features.Telephony.Outcomes;

/// <summary>
/// The outcomes every tenant starts with (TEL-B-11).
///
/// <para>
/// <b>Why a platform baseline at all, when the table is per tenant.</b> Two of the columns carry
/// BEHAVIOUR — <c>CountsAsContact</c> stops a lead's SLA clock, <c>RequiresCallbackDate</c> makes
/// a reminder mandatory — so an empty referential is not a blank slate an institution fills in at
/// leisure: it is a wrap-up screen with no options, which means no call can be qualified and
/// neither B-12's SLA nor B-13's follow-up ever fires. The baseline is what makes the feature work
/// on the day it is switched on.
/// </para>
///
/// <para>
/// <b>Labels in French and codes in English.</b> The code is an identifier a call row stores for
/// years and an API branches on; the label is what an agent reads and what an administrator may
/// rename. Mixing them would make a translation a data migration.
/// </para>
///
/// <para>
/// <c>IsSystem</c> on every one of them: a tenant may deactivate one, never delete it. A call
/// wrapped up last year points at the code, and removing it would leave that call explained by
/// nothing.
/// </para>
/// </summary>
internal static class TelCallOutcomeDefaults
{
    /// <summary>
    /// The code the missed-call follow-up of B-13 writes when it qualifies a call on an agent's
    /// behalf. Named rather than spelled inline: a typo there would be an outcome code no
    /// referential knows, and a wrap-up screen showing a blank.
    /// </summary>
    internal const string NotReached = "NOT_REACHED";

    /// <summary>
    /// What the seeder inserts, in display order.
    ///
    /// <para>
    /// <c>CountsAsContact</c> is true for the three that mean a human spoke to a human, and false
    /// for the rest — including <c>VOICEMAIL</c>, which is the one worth arguing about: leaving a
    /// message is not reaching somebody, and counting it would let an agent stop a lead's SLA
    /// clock without ever having a conversation.
    /// </para>
    ///
    /// <para>
    /// <c>RequiresCallbackDate</c> is true only for <c>CALLBACK</c>, where the whole point of the
    /// outcome is that a date was agreed. Making it true for <c>NO_ANSWER</c> would force a date
    /// onto every unanswered ring, which is how a mandatory field becomes a field everybody fills
    /// with today.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<TelCallOutcomeSeed> All =
    [
        new("REACHED", "Joint", CountsAsContact: true, RequiresCallbackDate: false, Order: 10),
        new("INTERESTED", "Joint — intéressé", true, false, 20),
        new("NOT_INTERESTED", "Joint — pas intéressé", true, false, 30),
        new("CALLBACK", "À rappeler", false, true, 40),
        new("VOICEMAIL", "Messagerie", false, false, 50),
        new(NotReached, "Pas joint", false, false, 60),
        new("WRONG_NUMBER", "Mauvais numéro", false, false, 70),
    ];
}

/// <param name="Order">
/// Spaced by ten so an administrator can insert an outcome of their own between two of ours
/// without renumbering the list.
/// </param>
internal sealed record TelCallOutcomeSeed(
    string Code, string Label, bool CountsAsContact, bool RequiresCallbackDate, int Order);
