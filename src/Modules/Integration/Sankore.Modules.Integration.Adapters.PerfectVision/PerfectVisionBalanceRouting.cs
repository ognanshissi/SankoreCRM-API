namespace Sankore.Modules.Integration.Adapters.PerfectVision;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// Where a Perfect Vision balance comes from (INT-28, criterion 2: « la lecture de solde passe
/// par la vue SQL en lecture seule si elle existe, sinon par le snapshot »).
///
/// <para>
/// <b>This decision is ours, not the vendor's</b> — which is why it is real code while the query
/// behind it is not. The condition is one configured name: an installation that exposes a
/// read-only view has one, an installation that does not has nothing. Nothing about that branch
/// depends on a document we do not have, so it is implemented and tested in full.
/// </para>
/// </summary>
public enum PerfectVisionBalanceSource
{
    /// <summary>
    /// The installation exposes a read-only view, so a live read is possible in principle. What
    /// it is NOT is writable from here: the columns of that view are the vendor's to publish, so
    /// the query itself is the part that waits (question 3 of
    /// <see cref="PerfectVisionSpecification.OpenQuestions"/>).
    /// </summary>
    ReadOnlyView,

    /// <summary>
    /// No view. The figure can only come from the last loaded extraction — the INT-21 snapshot,
    /// which <c>IntegrationModuleFacade</c> already serves with <c>CbsBalance.IsStale</c> set, so
    /// a counter clerk is told the number is old rather than shown it as current.
    /// </summary>
    Snapshot
}

/// <summary>
/// The routing rule of criterion 2, as a pure function of the connection's settings.
///
/// <para>
/// Static and settings-only on purpose: it is the one piece of INT-28 that can be verified today,
/// and a test of it must not need a database, a tenant or an adapter instance. The adapter calls
/// it; so does <see cref="PerfectVisionCapabilityMatrix"/>, so the matrix a screen reads and the
/// branch a call takes can never disagree.
/// </para>
/// </summary>
public static class PerfectVisionBalanceRouting
{
    /// <summary>
    /// <see cref="PerfectVisionBalanceSource.Snapshot"/> whenever no usable view name is
    /// configured — null, absent settings, or whitespace.
    ///
    /// <para>
    /// Whitespace counts as absent deliberately. A view name arrives through INT-03's settings
    /// form, and a cleared field that posts <c>" "</c> means the administrator removed the view;
    /// treating it as a name would declare a live-balance capability pointing at nothing.
    /// </para>
    ///
    /// <para>
    /// Null settings also mean <c>Snapshot</c> rather than an exception. The caller may be the
    /// capability matrix of a tenant that has configured no Perfect Vision connection at all, and
    /// "there is no view" is the correct answer to that, not a fault.
    /// </para>
    /// </summary>
    public static PerfectVisionBalanceSource ChooseFor(PerfectVisionSettings? settings)
        => HasReadOnlyView(settings)
            ? PerfectVisionBalanceSource.ReadOnlyView
            : PerfectVisionBalanceSource.Snapshot;

    /// <summary>The same condition, named, for the matrix and for assertions.</summary>
    public static bool HasReadOnlyView(PerfectVisionSettings? settings)
        => !string.IsNullOrWhiteSpace(settings?.BalanceViewName);
}
