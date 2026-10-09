namespace Sankore.Modules.Integration.Adapters.Amplitude;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What an Amplitude installation will serve, and in which mode (INT-31, criterion 3: « la matrice
/// de capacités est calculée selon la version »).
///
/// <para>
/// <b>The fully deliverable part of INT-31, and the valuable one.</b> The matrix does not describe
/// a wire format; it describes the shape of the integration — which operations exist and whether
/// each is answered in the second or deposited in a file and closed later. Both are known from the
/// release alone, without SBS, which is why this file carries no refusal.
/// </para>
///
/// <para>
/// A pure function of the inputs, so the matrix of an Up installation and the matrix of a pre-Up
/// one can be asserted side by side with no database in the way. The adapter's
/// <c>Capabilities</c> property is this function applied to the tenant's own connection.
/// </para>
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <para>
/// <b>WHY THE MODE IS THE SECOND INPUT.</b> Criterion 3 says "according to the version", and the
/// version is what decides whether an API exists at all. But the connection's own
/// <c>IntegrationMode</c> decides whether writes GO there: the dispatcher diverts a Batch
/// connection's commands to the file socle before any adapter is resolved. A matrix that ignored
/// the mode would declare <see cref="CapabilityMode.RealTime"/> writes for an installation whose
/// commands are deposited in a file — and <c>IntegrationModuleFacade</c>, reading that, would wait
/// for an answer that nothing in that path ever produces. <see cref="AmplitudeCarrierRouting"/>
/// holds the reconciliation and explains all six combinations.
/// </para>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class AmplitudeCapabilityMatrix
{
    /// <summary>
    /// The operations that become an <c>IntegrationCommand</c> — the writes.
    ///
    /// <para>
    /// The same five for both releases, because they are a property of the product and of INT-31's
    /// story (« crée mes clients et comptes dans mon CBS »): a pre-Up Amplitude accepts a customer,
    /// a KYC tier, an account and a loan application in its file, and an Up Amplitude accepts the
    /// same through a service. <b>What differs between the two releases is the MODE, never the
    /// list</b> — which is the claim criterion 3 makes, and the reason this list is shared rather
    /// than duplicated per version.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> Writes =
    [
        IntegrationCapability.CreateCustomer,
        IntegrationCapability.UpdateCustomer,
        IntegrationCapability.SetKycLevel,
        IntegrationCapability.OpenAccount,
        IntegrationCapability.SubmitLoanApplication,
    ];

    /// <summary>
    /// The reads an Amplitude <b>Up</b> installation serves, and only it — « lise leurs données »
    /// of INT-31's story.
    ///
    /// <para>
    /// Declared <see cref="CapabilityMode.RealTime"/> and never <c>Batch</c>, for the reason
    /// <see cref="AmplitudeCarrierRouting.ServesApiReads"/> gives: nothing in this socle defers a
    /// read to a file cycle, so a read either answers now or is not served at all. A pre-Up
    /// installation therefore declares NONE of these — its Customer 360 figures come from the
    /// INT-21 snapshot, and <c>ModeOf</c> answering null is what makes
    /// <c>IntegrationModuleFacade</c> fall back to it with <c>CbsBalance.IsStale</c> set, with no
    /// branch of its own.
    /// </para>
    ///
    /// <para>
    /// <b>Why these three and not more.</b> They are the reads INT-31's story names and that this
    /// adapter's ports can answer: <c>ICbsAccountPort</c> for the accounts and the balance,
    /// <c>ICbsLoanPort</c> for the loans. Transaction history and monthly flow
    /// (<c>ICbsTransactionPort</c>) are absent and the interface is not claimed — they belong to
    /// INT-13/INT-22's Temenos path and appear in none of INT-31's criteria. That absence has a
    /// consequence worth knowing before it is changed: <c>CbsSnapshotProjector</c> reads an
    /// undeclared <c>ReadMonthlyFlow</c> as a genuine ZERO, and that zero is compared against a
    /// KYC ceiling. It is harmless today — the projector abandons a refresh as soon as
    /// <c>ReadAccounts</c> refuses, so no row is written at all — but the day the Up catalogue
    /// arrives, a transaction service found in it must be added to this matrix in the SAME change
    /// as its mapper, never later.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="IntegrationCapability.ReadKycLevel"/> is absent from both releases</b>, and
    /// the adapter does not implement <c>ICbsKycLevelPort</c>. For a pre-Up installation the reason
    /// is the one that interface states itself — a batch CBS accepts the write and exposes no way
    /// to read the tier back. For Up the reason is narrower and worth stating: nothing tells us
    /// such a service exists, and that claim is not free. INT-21's criterion 4 compares the tier
    /// the CBS holds with M02's, and declaring a read we cannot make would put the compliance
    /// divergence check behind a service we have no evidence of. The fallback — inferring the CBS
    /// tier from the writes it acknowledged — is already in place and documents its own blind spot
    /// (<c>CbsKycLevelReader</c>).
    /// </para>
    ///
    /// <para>
    /// <see cref="IntegrationCapability.DebitAccount"/> and
    /// <see cref="IntegrationCapability.ReverseDebit"/> are absent from both for the plainest
    /// reason: they belong to ASS-05 and to no criterion of INT-31. An absent capability is refused
    /// by <c>IntegrationAdapterResolver.ResolvePort</c> before any call is made, which is a
    /// stronger guarantee than a method that says no.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> ApiReads =
    [
        IntegrationCapability.ReadAccounts,
        IntegrationCapability.ReadBalance,
        IntegrationCapability.ReadLoans,
    ];

    /// <summary>
    /// The matrix for one installation.
    ///
    /// <para>
    /// Two rules, each citing the path that reads it:
    /// </para>
    ///
    /// <list type="number">
    /// <item>every write in <see cref="Writes"/>, in the mode
    ///   <see cref="AmplitudeCarrierRouting.ChooseFor"/> resolves —
    ///   <see cref="CapabilityMode.RealTime"/> for Up's API services, else
    ///   <see cref="CapabilityMode.Batch"/>, which is a statement about the CARRIER and not about
    ///   speed: a <c>Batched</c> command is not a slow call, it is a file that has been deposited
    ///   and a command that stays open until an acknowledgement closes it (INT-24/INT-25);</item>
    /// <item>the three <see cref="ApiReads"/> as <see cref="CapabilityMode.RealTime"/> when, and
    ///   only when, the release exposes API services at all.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the writes are declared even though every call refuses today.</b> The matrix answers
    /// "what will this installation serve", which is what a screen needs in order to decide which
    /// buttons exist at all; the refusal answers "with what we hold today". Keeping them declared
    /// is what makes the arrival of the contract a file of field mappings rather than a change of
    /// architecture — and no button is offered that this installation cannot serve, because an
    /// Amplitude connection cannot be activated in the first place (see
    /// <c>AmplitudeAdapter.CheckHealthAsync</c>).
    /// </para>
    ///
    /// <para>
    /// <b>An incoherent configuration still yields the batch writes</b> rather than an empty
    /// matrix. A pre-Up release on an Api connection is a mode mistake, and the product does
    /// support those five operations; hiding every button would misdescribe Amplitude for a reason
    /// that is one field edit away. The mistake is reported where it can be acted on — as a
    /// configuration fault in the health answer, not as a silently narrowed matrix.
    /// </para>
    /// </summary>
    /// <param name="settings">
    /// The connection's settings, or <c>null</c> when none could be read. Null narrows to the batch
    /// writes: they are a property of the product, while the live reads are a property of the
    /// release. Narrowing on unreadable configuration is the only safe direction — a matrix that
    /// widened would offer live reads to an installation that has no API at all.
    /// </param>
    /// <param name="mode">
    /// The connection's mode, or <c>null</c> when there is no connection yet. Null narrows the same
    /// way, for the same reason.
    /// </param>
    public static IntegrationCapabilities For(AmplitudeSettings? settings, IntegrationMode? mode)
    {
        var modes = new Dictionary<IntegrationCapability, CapabilityMode>();

        var writeMode = AmplitudeCarrierRouting.ChooseFor(settings, mode) switch
        {
            AmplitudeCarrier.ApiServices => CapabilityMode.RealTime,
            _ => CapabilityMode.Batch,
        };

        foreach (var write in Writes)
            modes[write] = writeMode;

        if (AmplitudeCarrierRouting.ServesApiReads(settings))
        {
            foreach (var read in ApiReads)
                modes[read] = CapabilityMode.RealTime;
        }

        return new IntegrationCapabilities(modes);
    }
}
