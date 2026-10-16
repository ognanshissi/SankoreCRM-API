namespace Sankore.Modules.Integration.Adapters.Ringover;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a Ringover connection will serve, and in which mode (TEL-B-04).
///
/// <para>
/// <b>Deliverable without the test account, and the part every screen depends on.</b> The matrix
/// does not describe a wire format; it describes the shape of the integration — a hosted telephony
/// API, so its operations are synchronous and its events arrive by webhook. That much is knowable
/// from what the product IS, which is why this file carries no refusal and is the one place in
/// this assembly that makes a positive claim.
/// </para>
///
/// <para>
/// <b>Every entry is <see cref="CapabilityMode.RealTime"/>, and the mode is the thing here most
/// worth getting right.</b> It is a statement about the carrier, and this family has no batch
/// carrier at all: <c>RingoverSettings</c> is not a <c>BatchCapableSettings</c>, so a telephony
/// connection cannot be configured in <c>IntegrationMode.Batch</c>, and there is no file cycle for
/// a click-to-call to wait in. Declaring <c>Batch</c> would have the platform wait for an
/// acknowledgement that nothing ever produces, for an operation whose whole point is that an agent
/// is holding a handset.
/// </para>
///
/// <para>
/// A pure function, so the matrix can be asserted with no database in the way.
/// </para>
/// </summary>
public static class RingoverCapabilityMatrix
{
    /// <summary>
    /// What the product is expected to serve.
    ///
    /// <para>
    /// <b><see cref="IntegrationCapability.RecordingDeletion"/> is deliberately absent, and it is
    /// the only interesting entry in the list — the one that is not there.</b> Open question 6 is
    /// whether the API can destroy its own copy once SANKORE holds an encrypted one, and the
    /// plan's fallback is « purge côté SANKORE seule si l'API ne supprime pas ». A capability
    /// declared on a guess would be worse than the fallback rather than better: the retention job
    /// would call <c>DeleteRecordingAsync</c> on every expiring recording, every failure would be
    /// a row an operator has to triage, and — the part that matters — a compliance officer reading
    /// the capability list would believe the provider's copy was being destroyed. Not declaring it
    /// makes the retention's reach exactly what it is, which is what
    /// <see cref="RingoverFallbacks.RecordingDeletion"/> says in a sentence somebody can forward
    /// to a regulator.
    /// </para>
    ///
    /// <para>
    /// <see cref="IntegrationCapability.Recordings"/> IS declared, and the two are not the same
    /// claim: fetching a recording the provider offers is the ordinary shape of a telephony API,
    /// while deleting it is a privilege. A provider can have the first and not the second, and
    /// TEL-B-15's policy ships <c>Disabled</c> anyway — so the declaration grants nothing until a
    /// compliance officer states a legal basis.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<IntegrationCapability> LiveOperations =
    [
        // TEL-B-06 — the agent presses a button on a record and their handset rings.
        IntegrationCapability.ClickToCall,

        // TEL-B-07 — the provider tells us a call happened; TEL-B-08 journals it.
        IntegrationCapability.CallEvents,

        // TEL-B-14 — the catch-up sweep, which finds the calls whose webhook was lost.
        IntegrationCapability.CallHistory,

        // TEL-B-16 — fetching the audio the provider holds. NOT deleting it: see the remarks.
        IntegrationCapability.Recordings,

        // TEL-B-05 — the provider's own users, so CRM accounts can be linked to them.
        IntegrationCapability.UserDirectory,
    ];

    /// <summary>
    /// The matrix for one connection.
    /// </summary>
    /// <param name="settings">
    /// The row's settings, or <c>null</c> when they are of another kind's shape. Null answers
    /// <see cref="IntegrationCapabilities.None"/> rather than throwing: a screen asking what is
    /// available must not fail over a row somebody has to correct, and a row this adapter cannot
    /// read is a row it promises nothing about.
    /// </param>
    public static IntegrationCapabilities For(RingoverSettings? settings)
    {
        // The API host is the one thing a telephony connection cannot work without — the provider
        // serves regions on distinct hosts, so there is no default that is right for everybody,
        // and a tenant whose data must stay in one region cannot be pointed at another. A row
        // without it promises nothing, which is also what ConnectionSettingsValidator refuses to
        // let an administrator save.
        if (settings is null || string.IsNullOrWhiteSpace(settings.ApiHost))
            return IntegrationCapabilities.None;

        return IntegrationCapabilities.All(CapabilityMode.RealTime, [.. LiveOperations]);
    }
}
