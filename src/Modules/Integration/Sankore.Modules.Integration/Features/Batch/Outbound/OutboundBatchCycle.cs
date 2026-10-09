namespace Sankore.Modules.Integration.Features.Batch.Outbound;

/// <summary>
/// The cut-off arithmetic of INT-24 criterion 2, on its own so it can be pinned by a test without
/// a database, a clock or a connection.
///
/// <para>
/// <b>A cut-off is a time of day, so "generate at the cut-off" needs an instant.</b> The cycle of
/// an instant is the most recent occurrence of <c>CutOffTime</c> at or before it; a command is
/// eligible for that cycle only if it was created at or before the cycle's cut-off. Expressed
/// that way, "not before the cut-off" is one comparison instead of a window the caller has to get
/// right — and a command created five minutes after today's cut-off waits for tomorrow's file,
/// which is what a daily batch means.
/// </para>
///
/// <para>
/// <b>The cut-off is read as UTC, and that is a known deviation</b> from
/// <c>BatchCapableSettings.CutOffTime</c>'s "local time of day". Nothing in this repository
/// carries a tenant time zone — not <c>TenantInfo</c>, not the settings object — so a "local"
/// reading would have to invent one, and inventing it would make the file leave at an hour the
/// administrator did not choose while LOOKING correct. A single documented offset an operator can
/// account for is better than a guess. For West Africa (UTC+0 in Côte d'Ivoire, Senegal, Mali)
/// the two readings coincide; elsewhere the configured time is UTC until a tenant time zone
/// exists.
/// </para>
/// </summary>
internal static class OutboundBatchCycle
{
    /// <summary>
    /// The cut-off instant of the cycle <paramref name="now"/> falls in: today's occurrence once
    /// it has passed, yesterday's before that.
    /// </summary>
    internal static DateTimeOffset CurrentCutOff(DateTimeOffset now, TimeOnly cutOff)
    {
        var utc = now.ToUniversalTime();

        var today = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day,
            cutOff.Hour, cutOff.Minute, cutOff.Second, TimeSpan.Zero);

        return utc >= today ? today : today.AddDays(-1);
    }

    /// <summary>
    /// The first cut-off strictly after <paramref name="instant"/> — the moment a command created
    /// then becomes eligible to leave. Used for diagnostics and for the operator-facing detail of
    /// a "waiting for the cut-off" result: a message that names the hour is a message nobody
    /// reports as a bug.
    /// </summary>
    internal static DateTimeOffset NextCutOffAfter(DateTimeOffset instant, TimeOnly cutOff)
    {
        var current = CurrentCutOff(instant, cutOff);
        return current > instant.ToUniversalTime() ? current : current.AddDays(1);
    }

    /// <summary>
    /// <c>true</c> when a command created at <paramref name="createdAt"/> may leave in the file of
    /// the cycle whose cut-off is <paramref name="cycleCutOff"/>.
    ///
    /// <para>
    /// This is the single WRITTEN definition of the gate. <c>OutboundBatchFileGenerator</c>'s
    /// query MIRRORS it and cannot call it, because an expression over two
    /// <see cref="DateTimeOffset"/> values has to translate to SQL; the two must keep agreeing,
    /// the same arrangement <c>IntegrationCommand.IsDueAt</c> and the dispatcher's predicate
    /// already live with.
    /// </para>
    /// </summary>
    internal static bool IsEligible(DateTimeOffset createdAt, DateTimeOffset cycleCutOff)
        => createdAt.ToUniversalTime() <= cycleCutOff;
}
