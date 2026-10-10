namespace Sankore.Modules.Integration.Adapters.Orass;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Whether a submission can be attributed to this IMF at the insurer (ASS-06, criterion 3: « code
/// intermédiaire ou apporteur de l'IMF »).
/// </summary>
public enum OrassIntermediaryResolution
{
    /// <summary>
    /// An intermediary code is configured, so a submission could say who is submitting it. What
    /// this is NOT is a statement that the code EXISTS at the insurer, or that it belongs to the
    /// branch this connection claims — see the remarks on <see cref="OrassIntermediaryScope"/>,
    /// which explain why neither can be established without the specification and what has to be
    /// built the day it arrives.
    /// </summary>
    Resolved,

    /// <summary>
    /// No code. Every call must refuse, and refuse as a configuration fault the tenant's own
    /// administrator can clear in a minute — never as the pending specification, which is a
    /// conversation with ORSYS and the insurer.
    /// </summary>
    Missing
}

/// <summary>
/// The intermediary rule of criterion 3, as a pure function of the connection's settings — plus
/// the one place the <see cref="OrassBranch"/> is explained, because the branch and the code are
/// one fact and not two.
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <b>WHAT THE INTERMEDIARY CODE ACTUALLY SCOPES, AND HOW THAT DIFFERS FROM SAB'S
/// <c>Entity</c>.</b> The two questions look identical and are not, and getting the difference
/// right changes what the guard is FOR even though both end in the same fail-closed answer.
///
/// <c>SabSettings.Entity</c> is a <b>confidentiality</b> boundary. One Open SAB installation
/// serves several institutions of a network such as CIF, and the entity says whose book a call is
/// about: omit it and the call lands on whatever the installation defaults to, name the wrong one
/// and you READ another institution's customers. The leak crosses no HTTP boundary of ours, so
/// nothing of ours logs a denial — <c>SabEntityScope</c> is right to call that a security property.
///
/// <c>OrassSettings.IntermediaryCode</c> is an <b>attribution and provability</b> boundary. It is
/// this IMF's own identity as a distributor at the insurer — the apporteur under whose code the
/// business is booked. It does not select whose customers we may see: a subscription names its own
/// insured. What it decides is whose portfolio the policy lands in, who is owed the commission,
/// and — the part that matters most here — <b>whether anyone can later look the policy up as
/// ours</b>. So the ORASS failure mode is not primarily "another distributor's data leaks to us";
/// it is "this customer's cover exists somewhere nobody can attribute to this IMF", which in this
/// family is the worst outcome available: a counter clerk has told someone they are insured.
///
/// <b>WHICH IS WHY THE ANSWER IS STILL FAIL-CLOSED, BY A DIFFERENT ROUTE.</b> A submission with no
/// apporteur code has three possible fates at the insurer and all three are bad: rejected
/// (best — at least it is visible), parked in a suspense file for an operator to key in by hand
/// (which is precisely the « bordereaux saisis à la main » ASS-06 exists to abolish), or accepted
/// and attributed to the insurer's default intermediary (worst — the policy is real, the customer
/// is covered, and no screen of this IMF can find it). SANKORE would record the same success in
/// all three cases. Refusing instead costs an administrator one field in a form and a retry.
///
/// <b>AND THE BATCH CARRIER MAKES IT WORSE, WHICH IS AN ORASS-ONLY ARGUMENT.</b> SAB has one
/// carrier, a synchronous API, so a mis-scoped call at least has a chance of being refused in the
/// response. Criterion 2 puts ORASS on a file whenever the insurer's API is closed, and a
/// bordereau is a one-way deposit: there is nothing to read a refusal from. The deposit succeeds,
/// the transfer reports success, and the commands wait out their <c>AckTimeoutHours</c>. A guard on
/// the call path is therefore the ONLY place an unattributed submission can be stopped on the
/// batch carrier.
///
/// <b>WHAT THE ADAPTER GUARANTEES AT CALL TIME.</b> Not one port method, and not the health check,
/// may proceed past a connection whose <see cref="OrassSettings.IntermediaryCode"/> is absent. The
/// check is the FIRST thing the refusal chain does — before the carrier, before the credential,
/// before the specification refusal — and it fails closed. Unlike SAB, there is <b>no other half
/// to lean on</b>: <c>ConnectionSettingsValidator</c> makes <c>settings.entity</c> required for a
/// SAB connection but makes <c>settings.intermediaryCode</c> merely length-bounded for an ORASS
/// one, so for ORASS this is not the second of two guards — it is the only one. That gap is
/// reported rather than closed from here: adding <c>NotEmpty</c> to another slice's published
/// validator is a contract change, and the module's own idiom is that activation is the lock.
///
/// <b>WHAT CANNOT BE GUARANTEED, SAID OUT LOUD.</b> Presence is all that is verifiable here. Two
/// things are not, and both need the specification: whether the configured code exists at the
/// insurer, and whether it belongs to the UNDERTAKING this connection's
/// <see cref="OrassSettings.Branch"/> names. So the dangerous case this guard does not catch is a
/// well-formed code that belongs to another distributor or to the other branch, typed in by
/// mistake: it is indistinguishable from the right one until something answers. The day the
/// specification arrives, the obligation is therefore on <c>CheckHealthAsync</c> and not on a port
/// — it must prove the (branch, code) pair is one the insurer recognises, and leave the connection
/// unactivatable when it is not, because activation is the single gate the platform already makes
/// every command pass through.
///
/// <b>WHAT IS DELIBERATELY NOT CHECKED.</b> Character set, case, a prefix convention, a check
/// digit — nothing. Every one of those is a property of the insurer's own numbering, and a shape
/// rule invented here would refuse a legitimate apporteur code for a reason no document supports,
/// which is the same class of mistake as a guessed field name pointing the other way. The one
/// shape rule that exists — <c>MaximumLength(50)</c> in the validator — pre-dates this chantier
/// and nothing here relies on it.
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <b>THE BRANCH, AND WHY IT TRAVELS WITH THE CODE.</b> <see cref="OrassBranch.Iard"/> (non-life)
/// and <see cref="OrassBranch.Vie"/> (life) are not a label on one business. In the CIMA zone the
/// two must be carried by separate undertakings, so « l'assureur équipé d'ORASS®Suite » is in
/// practice two companies: two product sets, two claim processes, two sets of apporteur codes, and
/// in ORASS®Suite the branch is what decides which module answers — which is exactly what the
/// field's own comment in <c>ConnectionSettings</c> says. Three consequences follow, and they are
/// the reason this type holds both values:
///
/// <list type="number">
/// <item><b>The branch is part of the connection's identity, not a filter on one connection.</b> A
///   tenant distributing both life and non-life for the same insurer group needs TWO ORASS
///   connections, which the module already allows — a tenant may have several active
///   <see cref="IntegrationFamily.Insurance"/> connections, unlike core banking (ASS-01). It is
///   not a flag to be flipped on one row.</item>
/// <item><b>An apporteur code is branch-scoped.</b> It is issued by one undertaking, so the pair
///   (branch, code) is what identifies the IMF — a code valid at the IARD company means nothing at
///   the Vie one. That is the second reason presence is all this adapter can verify, and it is why
///   <see cref="Designation"/> prints the two together.</item>
/// <item><b>It does NOT narrow the capability matrix</b>, and that argument lives in
///   <see cref="OrassCapabilityMatrix"/> where a reader of the matrix will find it.</item>
/// </list>
///
/// <para>
/// Static and settings-only on purpose: it is one of the two halves of ASS-06 that can be verified
/// today, and a test of it must not need a database, a tenant or an adapter instance. The adapter
/// calls it; so does <see cref="OrassCapabilityMatrix"/>, so the matrix a screen reads and the
/// branch a call takes can never disagree — an installation whose submissions cannot be attributed
/// must not be shown buttons.
/// </para>
/// </summary>
public static class OrassIntermediaryScope
{
    /// <summary>
    /// <see cref="OrassIntermediaryResolution.Missing"/> whenever no usable intermediary code is
    /// configured — null, absent settings, or whitespace.
    ///
    /// <para>
    /// Whitespace counts as absent deliberately. A code arrives through INT-03's settings form, and
    /// a cleared field that posts <c>" "</c> means the administrator removed it; treating it as a
    /// value would submit business under a blank apporteur, which is precisely the "attributed to
    /// whatever the insurer defaults to" case.
    /// </para>
    ///
    /// <para>
    /// Null settings also mean <c>Missing</c> rather than an exception. The caller may be the
    /// capability matrix of a tenant that has configured no ORASS connection at all, and "there is
    /// no intermediary code" is the correct answer to that, not a fault.
    /// </para>
    /// </summary>
    public static OrassIntermediaryResolution ResolveFor(OrassSettings? settings)
        => IsAttributed(settings)
            ? OrassIntermediaryResolution.Resolved
            : OrassIntermediaryResolution.Missing;

    /// <summary>The same condition, named, for the matrix and for assertions.</summary>
    public static bool IsAttributed(OrassSettings? settings)
        => !string.IsNullOrWhiteSpace(settings?.IntermediaryCode);

    /// <summary>
    /// The intermediary code a submission would carry, or <c>null</c> when there is none.
    ///
    /// <para>
    /// Trimmed, because a settings form posts what was pasted and a trailing space is not part of
    /// an apporteur code. Trimming is the ONLY normalisation applied: case-folding or padding would
    /// be a guess about how the insurer compares the value, and a guess that turned one
    /// distributor's code into another's is the whole risk of this file.
    /// </para>
    ///
    /// <para>
    /// Nothing calls this today — no submission is built — and it exists anyway so that the value a
    /// future transport or bordereau writer puts on a record is read through the same function that
    /// decided the submission was allowed. A writer reading <c>settings.IntermediaryCode</c>
    /// directly is how a blank gets past a guard that said there was a code.
    /// </para>
    /// </summary>
    public static string? CodeOf(OrassSettings? settings)
        => IsAttributed(settings) ? settings!.IntermediaryCode!.Trim() : null;

    /// <summary>
    /// How an operator-facing message names the undertaking a call was meant for — the branch, and
    /// never the code.
    ///
    /// <para>
    /// The branch is in, because the two branches are two conversations with two companies and an
    /// operator reading a rejection needs to know which. The code is deliberately OUT: it would put
    /// a tenant's own configuration into every call-journal row and every rejection detail for no
    /// gain, since the administrator reading the message is looking at the field already. Nothing
    /// here is secret — the settings object is returned by the API — but a message should carry what
    /// is useful, and the branch is the half that is.
    /// </para>
    /// </summary>
    /// <param name="branch">
    /// The branch, or <c>null</c> when it could not be established — no connection, settings of
    /// the wrong shape, or a tenant whose several ORASS connections do not agree on one. Null names
    /// both, rather than picking one: sending somebody to the life company about a non-life
    /// submission is worse than naming two companies.
    /// </param>
    public static string Designation(OrassBranch? branch)
        => branch switch
        {
            OrassBranch.Iard => $"the {OrassBranch.Iard} (non-life) branch",
            OrassBranch.Vie => $"the {OrassBranch.Vie} (life) branch",
            _ => $"this insurer's {OrassBranch.Iard} and {OrassBranch.Vie} branches, which are "
                 + "separate undertakings in the CIMA zone and cannot be told apart from this "
                 + "connection",
        };

    /// <summary>
    /// The <c>Detail</c> an unattributed connection is refused with.
    ///
    /// <para>
    /// It names the field, what the code is for, the consequence, and — explicitly — that nothing
    /// is being waited on from ORSYS or from the insurer. Without that last clause the message is
    /// read as one more variation on "waiting for the specification", and a connection stays broken
    /// for a reason nobody owns. Reported with <see cref="IntegrationErrors.SettingsInvalid"/> for
    /// the same reason.
    /// </para>
    /// </summary>
    public static string MissingDetail()
        => "This ORASS connection configures no intermediary code (OrassSettings.IntermediaryCode "
           + "is empty), so no subscription or claim can be attributed to this institution at the "
           + "insurer. The code is the IMF's own identity as an apporteur: without it a submission "
           + "is rejected, parked for manual keying, or booked under the insurer's default "
           + "intermediary — and in the last case the policy is real while no screen of this "
           + "institution can find it. Set settings.intermediaryCode on the connection (INT-03), "
           + "for the branch this connection declares — nothing is being waited on from ORSYS or "
           + "from the insurer for this.";
}
