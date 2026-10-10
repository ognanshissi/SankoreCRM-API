namespace Sankore.Modules.Integration.Adapters.Amplitude;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which carrier an Amplitude installation's <b>writes</b> travel on (INT-31, criterion 2: « sur
/// les versions antérieures, il bascule sur le socle batch, selon
/// <c>AmplitudeSettings.AmplitudeVersion</c> »).
///
/// <para>
/// <b>This decision is ours, not the vendor's</b> — which is why it is real code while the records
/// and the service calls behind it are not. The inputs are one configured enum and the
/// connection's own mode; nothing about the branch depends on a document we do not have, so it is
/// implemented and tested in full.
/// </para>
/// </summary>
public enum AmplitudeCarrier
{
    /// <summary>
    /// Amplitude Up's API services, called per operation and answered in the second. What this is
    /// NOT is a list of services: which ones exist is question 2 of
    /// <see cref="AmplitudeSpecification.OpenQuestions"/>, and the catalogue is the part that
    /// waits.
    /// </summary>
    ApiServices,

    /// <summary>
    /// The INT-24/INT-25 batch socle: a command is <c>Batched</c> rather than sent, written into
    /// the connection's outbound file at its cut-off, and closed later by an acknowledgement.
    /// The narrow answer, and therefore the one every unknown resolves to.
    /// </summary>
    BatchSocle
}

/// <summary>
/// The carrier rule of criterion 2, as a pure function of the release and the connection's mode.
///
/// <para>
/// Static and input-only on purpose: it is one of the two halves of INT-31 that can be verified
/// today, and a test of it must not need a database, a tenant or an adapter instance. The adapter
/// calls it; so does <see cref="AmplitudeCapabilityMatrix"/>, so the matrix a screen reads and the
/// branch a call takes can never disagree.
/// </para>
///
/// ───────────────────────────────────────────────────────────────────────────────────────────────
/// <para>
/// <b>WHY THE MODE IS AN INPUT AT ALL, when the criterion names only the version.</b>
/// <see cref="AmplitudeSettings.AmplitudeVersion"/> and <c>IntegrationConnection.Mode</c> are two
/// separate fields that can disagree, and nothing in the module reconciles them. They are not
/// redundant: the version is a FACT about the installed release, while the mode is a CHOICE about
/// how SANKORE reaches it — and the mode is the field the socle actually routes on. The two paths
/// that matter read different ones, which is the whole of this class:
/// </para>
///
/// <list type="bullet">
/// <item><b>Writes follow the mode.</b> <c>ExecuteIntegrationCommandHandler</c> diverts a command
///   to the batch socle when <c>connection.Mode == Batch</c> and does it BEFORE resolving an
///   adapter — so on a Batch connection no adapter is ever asked, whatever the version says. A
///   matrix that declared an Up installation's writes <see cref="CapabilityMode.RealTime"/> on a
///   Batch connection would describe a path that does not exist.</item>
/// <item><b>Reads follow the version.</b> The read path (<c>IntegrationModuleFacade</c> →
///   <c>IntegrationAdapterResolver.ResolvePort</c> → the adapter) never consults the mode at all.
///   That is deliberate in this module and not an oversight: Perfect Vision is a Batch-mode
///   connection whose read-only view is declared <c>RealTime</c> and read live. So an Up
///   installation answers reads live even where its writes travel in files.</item>
/// </list>
/// ───────────────────────────────────────────────────────────────────────────────────────────────
///
/// <para>
/// <b>The six combinations, and what each one means.</b>
/// </para>
///
/// <list type="table">
/// <item><term>Up + Api</term><description>The intended Up shape. Writes on the API services,
///   reads live.</description></item>
/// <item><term>Up + Relay</term><description>Legitimate, and <b>its writes go in a file</b> —
///   which is not what one would wish and is what the platform does. The relay agent's FILE
///   carrier is delivered; its ORDER channel is not (INT-26's platform side), so
///   <c>ExecuteIntegrationCommandHandler</c> sends every relay write with batch coordinates to the
///   outbound socle, and <c>AmplitudeSettings</c> derives from <c>BatchCapableSettings</c>, so it
///   always has them. Declaring these writes real-time would have a screen offer a live button for
///   a write that leaves at a cut-off. The LIVE READS stay: the read path never consults the mode,
///   and an Up installation answers a query whoever carries it. Revisit on the day the order
///   channel exists — see <c>OutboundBatchCarrier</c>, which is the single definition both halves
///   of the platform now read.</description></item>
/// <item><term>Up + Batch</term><description>Legitimate, and a choice rather than a mistake: an
///   institution can run Up and still exchange files — the API module may not be licensed or
///   enabled, or its security policy may forbid inbound calls. The version says the services
///   exist; the mode says we do not use them for writes. So the writes become
///   <see cref="AmplitudeCarrier.BatchSocle"/> and the live reads stay.</description></item>
/// <item><term>Legacy + Batch</term><description>The only shape a pre-Up release has. Writes in
///   files, reads from the INT-21 snapshot.</description></item>
/// <item><term>Legacy + Api</term><description><b>Incoherent.</b> There is no API on this release
///   to call, and the dispatcher would hand every command to an adapter instead of enlisting it in
///   a file. An administrator fixes it by setting the mode to Batch — or by correcting the version
///   if the installation really is Up.</description></item>
/// <item><term>Legacy + Relay</term><description><b>Coherent, and this entry used to say the
///   opposite.</b> It was written when the dispatcher batched on <c>Mode == Batch</c> alone, so a
///   relay connection reached an adapter that had no API to call AND was invisible to the outbound
///   batch job — no file at all. Both halves were corrected in L8: the dispatcher routes a relay
///   write with batch coordinates to the socle, and the scheduled generation and deposit now scan
///   the same superset (<c>OutboundBatchCarrier</c>). So a pre-Up installation whose SFTP server
///   sits inside the institution's network may be expressed EITHER as mode Relay or as mode Batch
///   with a relay agent linked — <c>IntegrationFileTransportRouter</c> keys on both.</description></item>
/// </list>
///
/// <para>
/// <b>Where the incoherence rule lives, and why not in the validator.</b> It is enforced here and
/// surfaced by <c>AmplitudeAdapter.CheckHealthAsync</c> as a configuration fault distinct from the
/// missing contract — a mistake the administrator can correct themselves must not send them to
/// procurement. It is deliberately NOT a rule in
/// <c>ConnectionSettingsValidator</c>'s Amplitude block, for three reasons, in order of weight:
/// (1) a 422 cannot protect a row that did not come through the endpoint — a seed, a migration or
/// a support fix in SQL — while the health check is on the only path to activation; (2) the module's
/// own idiom is that a connection is created freely and ACTIVATION is the gate
/// (<c>IntegrationConnection.Activate</c> refuses without a passed check), so a half-configured
/// row is a normal intermediate state and not an error; (3) INT-03's current contract explicitly
/// admits the combination — <c>CreateConnectionValidatorTests.Legacy_amplitude_needs_none_because_it_has_no_api_at_all</c>
/// asserts that a Legacy settings object with the default mode (Api) is VALID, and
/// <c>UpdateConnectionValidatorTests</c> relies on the same default. Adding the rule there is a
/// one-line change plus a re-pointing of those two cases, and it would be an improvement — but it
/// is a change to another slice's published contract, so it is proposed rather than taken here.
/// Note for whoever takes it: the 422 key must be set with <c>.OverridePropertyName("mode")</c>,
/// as the shared "mode Batch requires file-based settings" rule does — the key is the path the
/// front-end puts the message under, and <c>mode</c> is the field the administrator must change.
/// </para>
/// </summary>
public static class AmplitudeCarrierRouting
{
    /// <summary>
    /// The carrier this installation's <b>writes</b> travel on.
    ///
    /// <para>
    /// <see cref="AmplitudeCarrier.ApiServices"/> only when the release exposes them AND the
    /// connection is not a file-exchanging one. Everything else — a pre-Up release, a Batch
    /// connection, settings that could not be read, a mode that could not be read — is
    /// <see cref="AmplitudeCarrier.BatchSocle"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Narrowing is the only safe direction on an unknown</b>, and it is free here:
    /// <c>Batch</c> is a statement that a command is deposited and closed later, which is the
    /// weaker promise. Resolving an unknown to <c>ApiServices</c> would declare live writes for an
    /// installation that may have no API at all, and a screen would offer an operation whose
    /// answer never arrives. <see cref="AmplitudeSettings"/> helps rather than hinders here: its
    /// <c>AmplitudeVersion</c> defaults to <see cref="AmplitudeVersion.Legacy"/>, so a row written
    /// before the field meant anything is already narrow.
    /// </para>
    /// </summary>
    /// <param name="settings">The connection's settings, or <c>null</c> when none could be read.</param>
    /// <param name="mode">
    /// The connection's mode, or <c>null</c> when there is no connection to read it from — a
    /// screen may ask what Amplitude supports before anything is configured.
    /// </param>
    public static AmplitudeCarrier ChooseFor(AmplitudeSettings? settings, IntegrationMode? mode)
    {
        // Spelled as "one of the two call carriers" rather than as "not Batch", and the difference
        // is not cosmetic: `mode is not IntegrationMode.Batch` is TRUE for null, so the negative
        // form hands an unknown mode the widest answer — exactly the direction the remarks above
        // forbid. A test pins it, because the mistake compiles and reads correctly.
        // Api ONLY, not `or Relay`. A relay write with batch coordinates is sent to the outbound
        // socle by the dispatcher, and AmplitudeSettings is BatchCapableSettings, so it always has
        // them: there is no reachable path by which a relay-mode Amplitude write becomes a call.
        // OutboundBatchCarrier is the platform's single definition of that, and this line must
        // agree with it — when it did not, the matrix declared real-time writes for commands that
        // left in a file.
        var callsAreMade = mode is IntegrationMode.Api;

        return settings?.AmplitudeVersion == AmplitudeVersion.Up && callsAreMade
            ? AmplitudeCarrier.ApiServices
            : AmplitudeCarrier.BatchSocle;
    }

    /// <summary>
    /// Whether this installation can answer a READ live — a question of the release alone.
    ///
    /// <para>
    /// Separate from <see cref="ChooseFor"/>, and the asymmetry is the finding rather than an
    /// inconsistency: a read is not a command, so nothing defers it to a file cycle. There is no
    /// <c>CommandType</c> for a read, the dispatcher is the only thing that routes on the mode, and
    /// the outbound file carries commands — so a read either answers now or comes from the INT-21
    /// snapshot. That is also why no read is ever declared in
    /// <see cref="CapabilityMode.Batch"/>: it would promise a carrier this socle does not have.
    /// </para>
    /// </summary>
    public static bool ServesApiReads(AmplitudeSettings? settings)
        => settings?.AmplitudeVersion == AmplitudeVersion.Up;

    /// <summary>
    /// Whether the configured release and the connection's mode can both be true at once.
    ///
    /// <para>
    /// False for exactly ONE case: a pre-Up release on an <see cref="IntegrationMode.Api"/>
    /// connection. There is no API on that release to call, so the dispatcher would hand every
    /// command to an adapter instead of enlisting it in a file, and the write would never move.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="IntegrationMode.Relay"/> used to be the second case, and no longer is.</b> It
    /// was incoherent while the dispatcher batched on <c>Mode == Batch</c> alone — a relay
    /// connection reached an adapter with nothing to call and was invisible to the outbound batch
    /// job, so it produced no file either. Both halves were corrected in L8 and now read one
    /// definition (<c>OutboundBatchCarrier</c>): a relay write with batch coordinates goes to the
    /// socle, and the scheduled generation and deposit see it. A pre-Up installation behind an
    /// agent is therefore a legitimate configuration, not a mistake to report.
    /// </para>
    ///
    /// <para>
    /// Unreadable settings are NOT reported as incoherent. "We could not read the version" and
    /// "the version contradicts the mode" are different faults with different fixes, and the
    /// health check tells them apart — collapsing them would point an administrator at a mode that
    /// may be perfectly correct.
    /// </para>
    /// </summary>
    public static bool IsModeCoherent(AmplitudeSettings? settings, IntegrationMode? mode)
    {
        if (settings is null || mode is null) return true;

        return settings.AmplitudeVersion != AmplitudeVersion.Legacy
            || mode is IntegrationMode.Batch or IntegrationMode.Relay;
    }

    /// <summary>
    /// The operator-facing statement of the incoherence, or <c>null</c> when there is none.
    ///
    /// <para>
    /// It names the two values that disagree and the fix, and it names the fix in both directions
    /// because only the administrator knows which field is the wrong one: an institution that
    /// really runs a pre-Up release needs the mode changed, while one that has upgraded to Up needs
    /// the version changed. A message that assumed either would be wrong half the time.
    /// </para>
    /// </summary>
    public static string? IncoherenceDetail(AmplitudeSettings? settings, IntegrationMode? mode)
    {
        if (IsModeCoherent(settings, mode)) return null;

        return $"this Amplitude connection is configured as release {AmplitudeVersion.Legacy} in "
               + $"mode {mode}, and the two cannot both hold: a pre-Up release exposes no API "
               + "service, so its commands must travel in files. Set the mode to "
               + $"{IntegrationMode.Batch} — or to {IntegrationMode.Relay} if the SFTP server sits "
               + "inside the institution's network, which deposits the file through the on-premise "
               + $"agent — or set the release to {AmplitudeVersion.Up} if this installation has "
               + "been upgraded.";
    }
}
