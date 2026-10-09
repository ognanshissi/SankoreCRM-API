namespace Sankore.Modules.Integration.Adapters.Orass;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What an ORASS installation will serve, and in which mode (ASS-06, criteria 2 and 3).
///
/// <para>
/// <b>The fully deliverable part of ASS-06, and the valuable one.</b> The matrix does not describe
/// a wire format; it describes the shape of the integration — which operations exist and whether
/// each is answered in the second or deposited in a bordereau and closed later. Both are known
/// from the insurer's coordinates and the connection's mode alone, without ORSYS, which is why
/// this file carries no refusal.
/// </para>
///
/// <para>
/// A pure function of its inputs, so the matrix of an API-fed insurer and the matrix of a
/// file-fed one can be asserted side by side with no database in the way. The adapter's
/// <c>CapabilitiesFor</c> is this function applied to the connection it was handed — one row, one
/// matrix. It used to be this function applied to every ORASS row of the tenant and then
/// intersected, because the contract's capability property carried no connection; L8 gave it one
/// and that intersection (<c>Narrowest</c>) was deleted.
/// </para>
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <para>
/// <b>WHAT THE BRANCH MEANS HERE — AND IT IS NOT A NARROWING.</b> <see cref="OrassBranch.Iard"/>
/// and <see cref="OrassBranch.Vie"/> are separate undertakings with separate products and separate
/// claim processes (the full argument is on <see cref="OrassIntermediaryScope"/>), so the tempting
/// move is to let the branch narrow this matrix. It must not, and the reason is worth stating
/// because the temptation will come back:
/// </para>
///
/// <list type="bullet">
/// <item><b>Every operation here exists on both branches.</b> A life undertaking and a non-life
///   one both subscribe a policy, cancel one, receive a claim, hold a portfolio and issue a
///   certificate. Nothing in the <see cref="IntegrationCapability"/> vocabulary distinguishes
///   them.</item>
/// <item><b>What differs between the branches is which PRODUCTS answer and how a claim is
///   handled</b> — a death certificate and a beneficiary payout on one side, an expert's report on
///   the other. Both are mapping concerns (<c>integration_mapping</c>'s product domain, and the
///   free-text <c>Nature</c> of <c>InsuranceClaimPayload</c>), and both are defined by the
///   specification we do not have. A matrix that narrowed on the branch would therefore be
///   encoding product knowledge nobody here possesses — the same class of mistake as a guessed
///   field name, arriving through a capability instead of through a mapper.</item>
/// <item><b>So the branch is carried into every refusal detail instead</b>
///   (<see cref="OrassSpecification.RefusalDetail"/>), where it tells an operator which of two
///   companies the conversation is with. That is what the field is worth today, and it is worth
///   exactly that much.</item>
/// </list>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public static class OrassCapabilityMatrix
{
    /// <summary>
    /// The operations that become an <c>IntegrationCommand</c> — the writes, and the whole of
    /// ASS-06's story: « les souscriptions et sinistres arrivent directement dans son
    /// back-office ».
    ///
    /// <para>
    /// The same three whichever carrier is in use, because they are a property of the business and
    /// not of the pipe: an insurer fed by bordereaux accepts a subscription, a cancellation and a
    /// claim declaration in its file, and one with an open API accepts the same through a call.
    /// <b>What differs between the two carriers is the MODE, never the list</b> — which is the
    /// claim criterion 2 makes, and the reason this list is shared rather than duplicated per
    /// carrier.
    /// </para>
    ///
    /// <para>
    /// These are exactly the three the dispatcher has a <c>CommandType</c> and a route for
    /// (<c>ExecuteIntegrationCommandHandler</c>: <c>SubscribePolicy</c>, <c>CancelPolicy</c>,
    /// <c>DeclareClaim</c>). Declaring a fourth write would declare an operation nothing can
    /// queue.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> Writes =
    [
        IntegrationCapability.SubscribePolicy,
        IntegrationCapability.CancelPolicy,
        IntegrationCapability.DeclareClaim,
    ];

    /// <summary>
    /// The reads an insurer with an open API serves, and only it.
    ///
    /// <para>
    /// Declared <see cref="CapabilityMode.RealTime"/> and never <c>Batch</c>, for the reason
    /// <see cref="OrassCarrierRouting.ServesApiReads"/> gives: nothing in this socle defers a read
    /// to a file cycle, so a read either answers now or is not served at all. A file-fed insurer
    /// therefore declares NONE of these — and in the insurance family that is not a stale figure
    /// but an ABSENT one, because this module owns no policy table and the insurance gateway skips
    /// a connection whose adapter does not declare the capability. Said plainly rather than
    /// implied: a bordereau-fed insurer's policies and claims do not appear on a 360 screen at
    /// all, which reads to an agent as "no cover". Closing that needs ASS-07's projection or the
    /// inbound extraction of question 4, and declaring a read we cannot serve would be a worse
    /// answer than an empty list.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="IntegrationCapability.IssueCertificate"/> is a read here</b>, which is worth
    /// a sentence because its name says write. <c>IInsurancePolicyPort.GetCertificateAsync</c>
    /// fetches a document the insurer has already produced; nothing is issued by us, there is no
    /// <c>CommandType</c> for it, and it is reached from the read path. On a file carrier it cannot
    /// be served at all — a bordereau is a one-way deposit and a certificate is handed to a
    /// customer standing at a counter.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> ApiReads =
    [
        IntegrationCapability.ReadPolicies,
        IntegrationCapability.ReadClaims,
        IntegrationCapability.IssueCertificate,
    ];

    /// <summary>
    /// The matrix for ONE installation.
    ///
    /// <para>
    /// Three rules, each citing the path that reads it:
    /// </para>
    ///
    /// <list type="number">
    /// <item><b>Nothing at all when the connection names no intermediary code.</b> That is
    ///   criterion 3 showing up in the matrix rather than only on the call path: an installation
    ///   whose submissions cannot be attributed to this institution is an installation no
    ///   operation may be attempted against, because every port method refuses on the intermediary
    ///   guard first (see <see cref="OrassIntermediaryScope"/>). Declaring capabilities anyway
    ///   would offer a screen six buttons whose every press answers a configuration error — and it
    ///   would do so for the one connection where the honest answer matters most, since the reason
    ///   the submission is refused is that the policy would otherwise end up in nobody's
    ///   portfolio. Same position <c>SabCapabilityMatrix</c> takes on a missing
    ///   entity.</item>
    /// <item>every write in <see cref="Writes"/>, in the mode
    ///   <see cref="OrassCarrierRouting.ChooseFor"/> resolves —
    ///   <see cref="CapabilityMode.RealTime"/> for an open API, else
    ///   <see cref="CapabilityMode.Batch"/>, which is a statement about the CARRIER and not about
    ///   speed: a <c>Batched</c> command is not a slow call, it is a file that has been deposited
    ///   and a command that stays open until an acknowledgement closes it (INT-24, INT-25);</item>
    /// <item>the three <see cref="ApiReads"/> as <see cref="CapabilityMode.RealTime"/> when, and
    ///   only when, the insurer has opened an API at all.</item>
    /// </list>
    ///
    /// <para>
    /// <b>What is absent and why.</b> <see cref="IntegrationCapability.PriceProduct"/> and
    /// <see cref="IntegrationCapability.CheckEligibility"/> are not declared, and
    /// <c>IInsuranceProductPort</c> is not implemented at all — the argument is on
    /// <c>OrassAdapter</c>, where the ports are chosen. Every core-banking capability belongs to
    /// another family and is not this adapter's to declare. An absent capability is refused by
    /// <c>IntegrationAdapterResolver.ResolvePort</c> before any call is made, which is a stronger
    /// guarantee than a method that says no.
    /// </para>
    ///
    /// <para>
    /// <b>An incoherent configuration still yields the batch writes</b> rather than an empty
    /// matrix, and the honest statement is that on such a row the declared mode describes NEITHER
    /// path: a mode-<c>Api</c> connection with no base URL — the one remaining incoherent shape —
    /// would be handed to an adapter by the dispatcher, while this matrix calls its writes
    /// <c>Batch</c> because there is no API to call. That contradiction IS what "incoherent" names;
    /// it is reported where it can be acted on — as a configuration fault in the health answer,
    /// with the fix in both directions — and not papered over by narrowing the matrix, because the
    /// three operations really are what ORASS does and hiding every button would misdescribe the
    /// insurer for a reason that is one field edit away.
    /// </para>
    ///
    /// <para>
    /// A mode-<see cref="IntegrationMode.Relay"/> row is NOT such a case, though it used to be
    /// read as one: its writes genuinely leave in a file, the scheduled pass genuinely produces and
    /// deposits that file (<c>OutboundBatchCarrier</c>), and so <c>Batch</c> is not a contradiction
    /// there but the plain truth. It is the same matrix as the equivalent <c>Batch</c> row, which is
    /// what a tenant that moved behind a relay agent should see — losing buttons for a reason that
    /// has nothing to do with what its insurer can do would be the mistake.
    /// </para>
    ///
    /// <para>
    /// <b>Why anything is declared when every call refuses today.</b> The matrix answers "what will
    /// this installation serve", which is what a screen needs in order to decide which buttons
    /// exist at all; the refusal answers "with what we hold today". Keeping the list declared is
    /// what makes the arrival of the specification a file of field mappings rather than a change of
    /// architecture — and no button is offered that cannot be served, because an ORASS connection
    /// cannot be activated in the first place (see <c>OrassAdapter.CheckHealthAsync</c>) and the
    /// insurance gateway reads policies and claims only from ACTIVE insurance connections.
    /// </para>
    /// </summary>
    /// <param name="settings">
    /// The connection's settings, or <c>null</c> when none could be read — no connection, or an
    /// ORASS row carrying another kind's settings. Null narrows to
    /// <see cref="IntegrationCapabilities.None"/>, because every entry here depends on being able
    /// to attribute the submission and unreadable settings attribute nothing. Narrowing on
    /// unreadable configuration is the only safe direction: a matrix that widened would offer a
    /// live subscription to an insurer this deployment cannot even name itself to.
    /// </param>
    /// <param name="mode">
    /// The connection's mode, or <c>null</c> when there is no connection yet. Null narrows the
    /// writes to <see cref="CapabilityMode.Batch"/>, for the same reason.
    /// </param>
    public static IntegrationCapabilities For(OrassSettings? settings, IntegrationMode? mode)
    {
        if (OrassIntermediaryScope.ResolveFor(settings) != OrassIntermediaryResolution.Resolved)
            return IntegrationCapabilities.None;

        var modes = new Dictionary<IntegrationCapability, CapabilityMode>();

        var writeMode = OrassCarrierRouting.ChooseFor(settings, mode) switch
        {
            OrassCarrier.ExternalApi => CapabilityMode.RealTime,
            _ => CapabilityMode.Batch,
        };

        foreach (var write in Writes)
            modes[write] = writeMode;

        if (OrassCarrierRouting.ServesApiReads(settings))
        {
            foreach (var read in ApiReads)
                modes[read] = CapabilityMode.RealTime;
        }

        return new IntegrationCapabilities(modes);
    }
}
