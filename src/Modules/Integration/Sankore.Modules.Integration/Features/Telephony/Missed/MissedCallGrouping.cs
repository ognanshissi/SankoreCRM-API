namespace Sankore.Modules.Integration.Features.Telephony.Missed;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// Groups the rings of one unanswered caller into one piece of work (TEL-B-13).
///
/// <para>
/// <b>Why grouping is the feature and not a nicety.</b> Somebody who cannot get through tries
/// again — three times in ten minutes is the normal human behaviour, not an anomaly — and without
/// grouping that is three entries in an agent's list, three notifications and, if B-13's optional
/// lead creation is on, three leads for one person. The agent then calls back three times or,
/// more likely, learns to ignore the list.
/// </para>
///
/// <para>
/// <b>Pure, and separated from the job for that reason.</b> The window arithmetic is the whole of
/// the decision and it is the part worth pinning case by case; the job around it reads rows and
/// publishes events.
/// </para>
/// </summary>
internal static class MissedCallGrouping
{
    /// <summary>
    /// Thirty minutes, as the story asks.
    ///
    /// <para>
    /// Measured between CONSECUTIVE attempts and not from the first one, which is the choice that
    /// matters: a caller trying every twenty minutes for two hours is one person still trying, and
    /// a fixed window from the first attempt would cut them into four groups at arbitrary points.
    /// A gap longer than this is somebody who gave up and came back later — a new conversation.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The groups, newest attempt last inside each.
    ///
    /// <para>
    /// Keyed on the remote number's BLIND INDEX, never on a decrypted number: the index is the
    /// only comparison that is correct here and the only one that does not put a number in this
    /// method's memory. Two callers who share a number — a family handset — are therefore one
    /// group, which is right: it is one handset the agency failed to answer.
    /// </para>
    ///
    /// <para>
    /// A call with NO usable index (an anonymised row, a withheld caller id) is its own group of
    /// one. Folding every such call together would report "forty missed calls from one caller"
    /// about forty different people.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<MissedCallGroup> Group(IEnumerable<MissedCallAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var groups = new List<MissedCallGroup>();

        // Ordered by caller then by time, so one pass closes a group when the gap opens. The
        // second key matters as much as the first: an unordered input makes "the gap since the
        // previous attempt" meaningless.
        var ordered = attempts
            .OrderBy(a => a.CallerKey, StringComparer.Ordinal)
            .ThenBy(a => a.StartedAt)
            .ToList();

        List<MissedCallAttempt>? current = null;

        foreach (var attempt in ordered)
        {
            var startsNewGroup =
                current is null
                || current[^1].CallerKey != attempt.CallerKey
                || attempt.StartedAt - current[^1].StartedAt > Window
                // An unidentifiable caller never joins anything, including another
                // unidentifiable one.
                || attempt.CallerKey.Length == 0;

            if (startsNewGroup)
            {
                if (current is not null) groups.Add(ToGroup(current));

                current = [attempt];

                continue;
            }

            current!.Add(attempt);
        }

        if (current is not null) groups.Add(ToGroup(current));

        return groups;
    }

    /// <summary>
    /// <b>The group speaks for its LAST attempt.</b> That is the call an agent should open — it
    /// carries the most recent reading of who the caller is, and if the earlier attempts were
    /// journalled before a lead for that number existed, only the last one is attached.
    /// </summary>
    private static MissedCallGroup ToGroup(List<MissedCallAttempt> attempts) => new(
        RepresentativeCallId: attempts[^1].CallId,
        AttemptCount: attempts.Count,
        FirstAttemptAt: attempts[0].StartedAt,
        LastAttemptAt: attempts[^1].StartedAt,
        EntityType: attempts[^1].EntityType,
        EntityId: attempts[^1].EntityId,
        CrmUserId: attempts[^1].CrmUserId);
}

/// <param name="CallerKey">
/// Lower-case hex of the remote number's blind index, or the empty string when there is none.
/// A string rather than the bytes so it can be ordered and compared without a custom comparer —
/// and so an empty value is spellable, which is what "caller id withheld" needs.
/// </param>
internal sealed record MissedCallAttempt(
    Guid CallId,
    string CallerKey,
    DateTimeOffset StartedAt,
    string? EntityType,
    Guid? EntityId,
    Guid? CrmUserId);

internal sealed record MissedCallGroup(
    Guid RepresentativeCallId,
    int AttemptCount,
    DateTimeOffset FirstAttemptAt,
    DateTimeOffset LastAttemptAt,
    string? EntityType,
    Guid? EntityId,
    Guid? CrmUserId);

/// <summary>Which statuses count as "nobody answered" (TEL-B-13).</summary>
internal static class MissedCallStatuses
{
    /// <summary>
    /// <c>Missed</c> and <c>Voicemail</c>, and deliberately NOT <c>Failed</c>.
    ///
    /// <para>
    /// A failed call is an operator refusal or an invalid number — there is nobody waiting to be
    /// called back, and putting those in an agent's follow-up list would fill it with work that
    /// cannot be done. Voicemail IS included: somebody left a message, which is the clearest
    /// possible request for a call back.
    /// </para>
    /// </summary>
    internal static bool IsMissed(TelCallStatus status)
        => status is TelCallStatus.Missed or TelCallStatus.Voicemail;
}
