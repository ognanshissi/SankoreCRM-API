namespace Sankore.Modules.Integration.Adapters.PerfectVision;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a Perfect Vision installation will serve, and in which mode (INT-28, criterion 3: « la
/// matrice de capacités déclare les modes réellement supportés »).
///
/// <para>
/// <b>The fully deliverable part of INT-28, and the valuable one.</b> The matrix does not describe
/// a wire format; it describes the shape of the integration — writes travel in a file and are
/// closed later by an acknowledgement, and one read may be live if the installation exposes a
/// view. Both of those are known without the vendor's document, which is why this file carries no
/// refusal.
/// </para>
///
/// <para>
/// A pure function of the settings, so the matrix of a tenant with a view and the matrix of a
/// tenant without one can be asserted side by side with no database in the way. The adapter's
/// <c>Capabilities</c> property is this function applied to the tenant's own connection.
/// </para>
/// </summary>
public static class PerfectVisionCapabilityMatrix
{
    /// <summary>
    /// The writes Perfect Vision serves through the batch cycle.
    ///
    /// <para>
    /// <see cref="CapabilityMode.Batch"/> is a statement about the carrier and not about speed:
    /// a <c>Batched</c> command is not a slow call, it is a file that has been deposited and a
    /// command that stays open until an acknowledgement closes it (INT-24/INT-25). Declaring any
    /// of these <see cref="CapabilityMode.RealTime"/> would make <c>IntegrationModuleFacade</c>
    /// wait for a synchronous answer that nothing in this integration ever produces.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> BatchWrites =
    [
        IntegrationCapability.CreateCustomer,
        IntegrationCapability.UpdateCustomer,
        IntegrationCapability.SetKycLevel,
        IntegrationCapability.OpenAccount,
        IntegrationCapability.SubmitLoanApplication,
    ];

    /// <summary>
    /// The matrix for one installation.
    ///
    /// <para>
    /// <b><see cref="IntegrationCapability.ReadBalance"/> is present only when a view is
    /// configured</b>, and then as <see cref="CapabilityMode.RealTime"/> — a view query answers in
    /// the second, unlike everything else here. The pairing matters downstream:
    /// <c>IntegrationModuleFacade</c> reads <c>IsRealTime(ReadBalance)</c> and goes straight to
    /// the snapshot when it is false, so an installation with no view gets INT-28's « sinon le
    /// snapshot » from the matrix alone, with no branch of its own.
    /// </para>
    ///
    /// <para>
    /// <b>What is absent and why.</b> <c>ReadAccounts</c>, <c>ReadTransactions</c>,
    /// <c>ReadMonthlyFlow</c> and <c>ReadLoans</c> are not declared: a batch-file core banking
    /// system answers no query. Callers of those reads fall back to the INT-21 snapshot — and for
    /// an installation like this one that snapshot stays EMPTY, which is worth stating rather than
    /// implying. <c>CbsSnapshotProjector</c> populates it by calling the external system's read
    /// ports, so an adapter declaring none refreshes nothing; and the inbound extraction does not
    /// fill the gap, because <c>ExtractionApplier</c> deliberately reads only identifiers from the
    /// file and never its figures (an unsigned file on an SFTP server must not write a customer's
    /// financial position). So the honest statement is that a batch-only CBS has no financial read
    /// model at all until either a read-only view exists or INT-34's reconciliation gives one a
    /// trustworthy source. <c>ReadKycLevel</c> is absent for the reason <c>ICbsKycLevelPort</c>
    /// states itself — a batch CBS accepts the write and exposes no way to read the tier back.
    /// <c>DebitAccount</c> and <c>ReverseDebit</c> belong to ASS-05 and are not in INT-28's
    /// criteria. An absent capability is refused by <c>IntegrationAdapterResolver.ResolvePort</c>
    /// before any call is made, which is a stronger guarantee than a method that says no.
    /// </para>
    ///
    /// <para>
    /// <b>Why the writes are declared even though every call refuses today.</b> The matrix answers
    /// "what will this installation serve", which is what a screen needs in order to decide which
    /// buttons exist at all; the refusal answers "with what we hold today". Keeping the writes
    /// declared is what makes the arrival of the specification a file of field mappings rather than
    /// a change of architecture — and no button is offered that this installation cannot serve,
    /// because an unreachable Perfect Vision connection cannot be activated in the first place
    /// (see <c>PerfectVisionAdapter.CheckHealthAsync</c>).
    /// </para>
    /// </summary>
    /// <param name="settings">
    /// The connection's settings, or <c>null</c> when none could be read. Null narrows the matrix
    /// — the batch writes stay, since they are a property of the product, while
    /// <c>ReadBalance</c> drops out because it is a property of the installation. Narrowing on
    /// unreadable settings is the only safe direction: a matrix that widened on missing
    /// configuration would offer a live balance read to an installation that has no view.
    /// </param>
    public static IntegrationCapabilities For(PerfectVisionSettings? settings)
    {
        var modes = new Dictionary<IntegrationCapability, CapabilityMode>();

        foreach (var write in BatchWrites)
            modes[write] = CapabilityMode.Batch;

        if (PerfectVisionBalanceRouting.ChooseFor(settings) == PerfectVisionBalanceSource.ReadOnlyView)
            modes[IntegrationCapability.ReadBalance] = CapabilityMode.RealTime;

        return new IntegrationCapabilities(modes);
    }
}
