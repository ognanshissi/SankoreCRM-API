namespace Sankore.Modules.Integration.Adapters.Orass;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which carrier an ORASS installation's <b>writes</b> travel on (ASS-06, criterion 2: « repli en
/// mode batch (bordereaux par fichier) sur le socle commun si l'API n'est pas ouverte chez
/// l'assureur »).
///
/// <para>
/// <b>This decision is ours, not ORSYS's</b> — which is why it is real code while the requests and
/// the bordereau records behind it are not. The inputs are one configured coordinate and the
/// connection's own mode; nothing about the branch depends on a document we do not have, so it is
/// implemented and tested in full.
/// </para>
/// </summary>
public enum OrassCarrier
{
    /// <summary>
    /// A call is made and an answer comes back, in the second.
    ///
    /// <para>
    /// <b>One value for what criterion 1 names as two surfaces</b> — « l'API externe ou le module
    /// Bancassurance d'ORASS ». That is deliberate and not a simplification: from this socle's
    /// point of view the two are the same carrier (a synchronous call this process makes), and
    /// what differs between them is the operation list and the wire shape, which is exactly the
    /// part no document here defines. Nothing in <see cref="OrassSettings"/> distinguishes them
    /// either, and inventing a discriminator would be inventing a fact about the insurer's
    /// installation — so which of the two an insurer exposes is question 2 of
    /// <see cref="OrassSpecification.OpenQuestions"/> and not an enum value.
    /// </para>
    /// </summary>
    ExternalApi,

    /// <summary>
    /// The INT-24/INT-25 batch socle: a command is <c>Batched</c> rather than sent, written into
    /// the connection's outbound file at its cut-off, and closed later by an acknowledgement.
    /// The narrow answer, and therefore the one every unknown resolves to.
    ///
    /// <para>
    /// <b>Routing here is deliverable; the bordereau's CONTENT is not.</b> The socle will produce
    /// a file for this connection — but through <c>DelimitedOutboundBatchFormatter</c>, whose own
    /// remarks say what it is: a complete, self-describing projection of what the platform owes,
    /// one delimited line per command, and expressly NOT a guess at anybody's layout. An insurer's
    /// ORASS cannot load it. So this value says "a file is the carrier", never "a bordereau can be
    /// written" — the layout and the acknowledgement format are questions 3 and 4, and the health
    /// gate (see <c>OrassAdapter.CheckHealthAsync</c>) is what keeps such a file from ever being
    /// deposited in the meantime.
    /// </para>
    /// </summary>
    BatchSocle
}

/// <summary>
/// The carrier rule of criterion 2, as a pure function of the connection's coordinates and its
/// mode.
///
/// <para>
/// Static and input-only on purpose: it is one of the two halves of ASS-06 that can be verified
/// today, and a test of it must not need a database, a tenant or an adapter instance. The adapter
/// calls it; so does <see cref="OrassCapabilityMatrix"/>, so the matrix a screen reads and the
/// branch a call takes can never disagree.
/// </para>
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <para>
/// <b>WHAT STANDS IN FOR AMPLITUDE'S VERSION FIELD.</b> INT-31 could read
/// <c>AmplitudeSettings.AmplitudeVersion</c>, because a release either exposes API services or
/// does not. ORASS has no such field and must not grow one: criterion 2's condition is not a
/// property of the product — ORASS®Suite has an external API and a Bancassurance module — but of
/// the INSTALLATION in front of us, « si l'API n'est pas ouverte chez l'assureur ». What says that
/// is already in the settings: <see cref="OrassSettings.BaseUrl"/> is <c>string?</c> and
/// <c>ConnectionSettingsValidator</c>'s ORASS block deliberately does NOT require it, unlike the
/// SAB block one screen above which makes <c>settings.baseUrl</c> mandatory. A connection with no
/// base URL is a connection to an insurer that has opened no API, and that is the whole of the
/// condition. No new field, no new enum, and the thing the criterion names is the thing that is
/// read.
/// </para>
///
/// <para>
/// <b>WHY THE MODE IS THE SECOND INPUT.</b> <c>BaseUrl</c> and <c>IntegrationConnection.Mode</c>
/// are two separate fields that can disagree, and nothing in the module reconciles them. They are
/// not redundant: the base URL is a FACT about what the insurer exposes, while the mode is a
/// CHOICE about how SANKORE reaches it — and the mode is the field the socle actually routes on.
/// The two paths that matter read different ones, which is the whole of this class:
/// </para>
///
/// <list type="bullet">
/// <item><b>Writes follow the mode.</b> <c>ExecuteIntegrationCommandHandler</c> diverts a command
///   to the batch socle on the connection's mode and does it BEFORE resolving an adapter — so on
///   such a connection no adapter is ever asked, whatever the coordinates say. A matrix that
///   declared an API installation's writes <see cref="CapabilityMode.RealTime"/> on a
///   file-exchanging connection would describe a path that does not exist.</item>
/// <item><b>Reads follow the coordinates.</b> The read path (<c>IntegrationModuleFacade</c>'s
///   insurance gateway → <c>ResolveInsurancePort</c> → the adapter) never consults the mode at
///   all. That is deliberate in this module and not an oversight: Perfect Vision is a Batch-mode
///   connection whose read-only view is declared <c>RealTime</c> and read live. So an insurer with
///   an open API answers reads live even where its subscriptions travel in bordereaux.</item>
/// </list>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>THE SIX COMBINATIONS, AND WHAT EACH ONE MEANS.</b> Two coordinate states by three modes.
/// </para>
///
/// <list type="table">
/// <item><term>API open + <see cref="IntegrationMode.Api"/></term><description>The intended API
///   shape of criterion 1. Subscriptions and claims on the insurer's API, reads live.</description></item>
/// <item><term>API open + <see cref="IntegrationMode.Batch"/></term><description>Legitimate, and a
///   choice rather than a mistake: an insurer can expose an API and still be fed by file — the
///   module may not be licensed for this intermediary, or the insurer's own policy may forbid
///   inbound calls from a distributor. The coordinates say the API exists; the mode says we do not
///   use it for writes. So the writes become <see cref="OrassCarrier.BatchSocle"/> and the live
///   reads stay.</description></item>
/// <item><term>API closed + <see cref="IntegrationMode.Batch"/></term><description>Criterion 2's
///   own case, and the one ASS-06 exists to provide for: bordereaux by file on the shared socle,
///   and no read at all.</description></item>
/// <item><term>API closed + <see cref="IntegrationMode.Api"/></term><description><b>Incoherent.</b>
///   There is no API to call, and the dispatcher would hand every subscription to an adapter
///   instead of enlisting it in a file. An administrator fixes it by setting the mode to
///   <c>Batch</c> and giving the connection its file coordinates — or by filling in the base URL
///   if the insurer really has opened one.</description></item>
/// <item><term>Either + <see cref="IntegrationMode.Relay"/></term><description>Legitimate, and
///   <b>its writes go in a file</b> — the same insurer reached through the on-premise agent,
///   because SANKORE opens no port into the institution's network (INT-26). Not a call, and not a
///   fault either; see the next paragraph, which is worth reading before changing this
///   row.</description></item>
/// </list>
///
/// <para>
/// <b>WHY <see cref="IntegrationMode.Relay"/> IS NOT A CALL CARRIER — AND WHY IT IS NOT A FAULT
/// EITHER, THOUGH IT WAS.</b> <see cref="OrassSettings"/> derives from
/// <c>BatchCapableSettings</c>, and <c>OutboundBatchCarrier.LeavesInAFile</c> — the module's single
/// definition of "this connection's writes leave in a file" — answers true for mode <c>Batch</c>
/// and for mode <c>Relay</c> with file-capable settings. Every ORASS connection satisfies the
/// second clause. So a relay write is deposited, never dialled: a routing that called it
/// <see cref="OrassCarrier.ExternalApi"/> would contradict the dispatcher outright, which is why
/// <see cref="ChooseFor"/> spells its predicate <c>Api</c> and not <c>Api or Relay</c>.
/// </para>
///
/// <para>
/// That much was always true. What has changed is the verdict: this class used to report every
/// relay ORASS connection as <b>incoherent</b>, because the two halves of the platform disagreed
/// about it. The dispatcher enlisted such a write into the socle, while
/// <c>OutboundBatchOrchestratorJob</c> and <c>GenerateTenantOutboundBatchFilesJob</c> scanned
/// <c>Mode == Batch</c> alone — so the bordereau was never generated or deposited, and every
/// subscription waited out its <c>AckTimeoutHours</c> for an acknowledgement to a file that did not
/// exist. Refusing was the right answer to a platform that could not carry the write. <b>That split
/// is closed</b>: all three paths now read <c>OutboundBatchCarrier</c>, a relay connection is
/// generated and deposited by the scheduled pass like any other, and the honest verdict is
/// therefore that the shape works. "The insurer's SFTP server sits inside the institution's
/// network" is now expressible either way — mode <c>Relay</c>, or mode <c>Batch</c> with a relay
/// agent linked, which is what <c>IntegrationFileTransportRouter</c> keys on
/// (<c>RelayAgentId</c>, not the mode).
/// </para>
///
/// <para>
/// <b>There is no longer any divergence from <c>AmplitudeCarrierRouting</c> to look for.</b> An
/// earlier version of this file argued at length that ORASS "parts company" with it over this mode,
/// and recorded the correction as reported-not-taken. It has since been taken, platform-side and in
/// that adapter: Amplitude now spells its own predicate <c>Api</c> only, for the reason above, and
/// treats a relay connection as coherent. The two adapters agree on both halves — a relay write is
/// a file write, and a file write is not a fault — so a reader should expect to find them saying
/// the same thing. The remaining asymmetry between them is about their own products, not about
/// this mode: Amplitude reads a release enum, ORASS reads whether the insurer opened an API at all.
/// </para>
///
/// <para>
/// <b>Where the incoherence rule lives, and why not in the validator.</b> It is enforced here and
/// surfaced by <c>OrassAdapter.CheckHealthAsync</c> as a configuration fault distinct from the
/// missing specification — a mistake the administrator can correct themselves must not send them
/// to the insurer. It is deliberately NOT a rule in <c>ConnectionSettingsValidator</c>'s ORASS
/// block, for the three reasons <c>AmplitudeCarrierRouting</c> sets out and which hold here
/// unchanged: a 422 cannot protect a row that did not come through the endpoint (a seed, a
/// migration, a support fix in SQL), the module's idiom is that a connection is created freely and
/// ACTIVATION is the gate, and INT-03's published contract currently admits the combination. The
/// chain of guarantee is the one every blocked adapter relies on: incoherent row → health check
/// <c>Unhealthy</c> → activation refused → no command can be queued.
/// </para>
/// </summary>
public static class OrassCarrierRouting
{
    /// <summary>
    /// The carrier this installation's <b>writes</b> travel on.
    ///
    /// <para>
    /// <see cref="OrassCarrier.ExternalApi"/> only when the insurer exposes API coordinates AND
    /// the connection is one whose writes this process actually sends. Everything else — no base
    /// URL, a Batch connection, a Relay connection, settings that could not be read, a mode that
    /// could not be read — is <see cref="OrassCarrier.BatchSocle"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Narrowing is the only safe direction on an unknown</b>, and it is nearly free here:
    /// <c>BatchSocle</c> is a statement that a command is deposited and closed later, which is the
    /// weaker promise. Resolving an unknown to <c>ExternalApi</c> would declare live subscriptions
    /// for an insurer that may have opened nothing, and a screen would offer a counter clerk a
    /// "subscribe" button whose answer never arrives — in this family that is a customer told at
    /// the counter that they are covered.
    /// </para>
    /// </summary>
    /// <param name="settings">The connection's settings, or <c>null</c> when none could be read.</param>
    /// <param name="mode">
    /// The connection's mode, or <c>null</c> when there is no connection to read it from — a
    /// screen may ask what ORASS supports before anything is configured.
    /// </param>
    public static OrassCarrier ChooseFor(OrassSettings? settings, IntegrationMode? mode)
    {
        // Spelled as "the one mode on which this process sends a write itself" rather than as
        // "not Batch", and the difference is not cosmetic: `mode is not IntegrationMode.Batch` is
        // TRUE for null AND for Relay, so the negative form hands both an unknown mode and a
        // relayed connection the widest answer — exactly the direction the remarks above forbid.
        // A test pins it, because the mistake compiles and reads correctly.
        var callsAreMade = mode is IntegrationMode.Api;

        return HasApiCoordinates(settings) && callsAreMade
            ? OrassCarrier.ExternalApi
            : OrassCarrier.BatchSocle;
    }

    /// <summary>
    /// Whether this insurer has opened an API at all — the condition criterion 2 turns on, named.
    ///
    /// <para>
    /// Whitespace counts as absent deliberately. A base URL arrives through INT-03's settings form,
    /// and a cleared field that posts <c>" "</c> means the administrator removed the coordinates;
    /// treating it as an address would declare a live subscription capability pointing at nothing.
    /// </para>
    ///
    /// <para>
    /// Presence only — the value's SHAPE is the validator's business and already its rule
    /// (<c>settings.baseUrl</c> must be an absolute https URL when non-empty). Re-checking it here
    /// would put two definitions of "usable coordinates" in the repository, and the one that
    /// disagreed would be the one nobody was looking at.
    /// </para>
    /// </summary>
    public static bool HasApiCoordinates(OrassSettings? settings)
        => !string.IsNullOrWhiteSpace(settings?.BaseUrl);

    /// <summary>
    /// Whether this installation can answer a READ live — a question of the insurer's coordinates
    /// alone.
    ///
    /// <para>
    /// Separate from <see cref="ChooseFor"/>, and the asymmetry is the finding rather than an
    /// inconsistency: a read is not a command, so nothing defers it to a file cycle. There is no
    /// <c>CommandType</c> for a read, the dispatcher is the only thing that routes on the mode, and
    /// the outbound bordereau carries commands — so a read either answers now or is not served.
    /// That is also why no read is ever declared in <see cref="CapabilityMode.Batch"/>: it would
    /// promise a carrier this socle does not have.
    /// </para>
    ///
    /// <para>
    /// <b>And in the insurance family there is no snapshot to fall back on.</b> Core banking has
    /// one (INT-21), which is why <c>PerfectVisionAdapter</c> can refuse a live balance and still
    /// leave a figure on the screen. This module owns no policy table — the insurance gateway reads
    /// every policy and claim live from each insurer and simply skips a connection whose adapter
    /// does not declare the capability. So a file-fed insurer's policies are not stale on a 360
    /// screen, they are ABSENT, which reads to an agent as "this customer has no cover". Closing
    /// that needs either ASS-07's projection or the inbound extraction of question 4; it is not
    /// something this adapter can paper over, and declaring a read it cannot serve would be a worse
    /// answer than an empty list.
    /// </para>
    /// </summary>
    public static bool ServesApiReads(OrassSettings? settings) => HasApiCoordinates(settings);

    /// <summary>
    /// Whether the configured coordinates and the connection's mode can both be true at once.
    ///
    /// <para>
    /// False for exactly ONE case: an insurer with no API coordinates on an
    /// <see cref="IntegrationMode.Api"/> connection. It routes a subscription to an adapter that
    /// has no API to call, so the write can never move.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="IntegrationMode.Relay"/> was the second case, and is not one any more.</b> It
    /// was incoherent because the dispatcher enlisted a relay write into the batch socle while the
    /// outbound generation pass scanned <c>Mode == Batch</c> alone — so the bordereau was never
    /// written and every subscription waited out its acknowledgement timeout. That split was
    /// closed: both halves now read one definition, <c>OutboundBatchCarrier</c>, and a relay
    /// connection with file coordinates is generated and deposited like any other. Reaching an
    /// insurer whose SFTP server sits inside the institution's network is therefore expressible
    /// either way — mode <c>Relay</c>, or mode <c>Batch</c> with a relay agent linked, which is
    /// what <c>IntegrationFileTransportRouter</c> keys on.
    /// </para>
    ///
    /// <para>
    /// Unreadable settings are NOT reported as incoherent. "We could not read the coordinates" and
    /// "the coordinates contradict the mode" are different faults with different fixes, and the
    /// health check tells them apart — collapsing them would point an administrator at a mode that
    /// may be perfectly correct. A null MODE is not incoherent either: there is no connection, so
    /// there is nothing to contradict.
    /// </para>
    /// </summary>
    public static bool IsModeCoherent(OrassSettings? settings, IntegrationMode? mode)
    {
        if (settings is null || mode is null) return true;

        return mode switch
        {
            IntegrationMode.Api => HasApiCoordinates(settings),

            // Batch and Relay both reach the outbound socle, which now sees them both.
            _ => true,
        };
    }

    /// <summary>
    /// The operator-facing statement of the incoherence, or <c>null</c> when there is none.
    ///
    /// <para>
    /// There is one case left, and it names the two values that disagree plus the fix <b>in both
    /// directions</b> — because only the administrator knows which of the two fields is the wrong
    /// one: an insurer that really has opened nothing needs the mode changed, while one that has
    /// opened an API needs the base URL filled in. A message that assumed either would be wrong
    /// half the time.
    /// </para>
    /// </summary>
    public static string? IncoherenceDetail(OrassSettings? settings, IntegrationMode? mode)
    {
        if (IsModeCoherent(settings, mode)) return null;

        return $"this ORASS connection is in mode {IntegrationMode.Api} and carries no base URL, "
               + "and the two cannot both hold: with no API open at the insurer there is nothing "
               + "to call, so its subscriptions and claims must travel in bordereaux. Set the mode "
               + $"to {IntegrationMode.Batch} and give the connection its file coordinates — or "
               + "set settings.baseUrl if this insurer has opened its API or its Bancassurance "
               + "module to this intermediary.";
    }
}
