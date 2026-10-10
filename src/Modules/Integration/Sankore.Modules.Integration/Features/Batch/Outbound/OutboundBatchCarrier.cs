namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The single definition of <b>"this connection's writes leave in a file"</b> (INT-24, INT-26).
///
/// <para>
/// It exists because the definition was duplicated and the two copies drifted. The dispatcher
/// learned, in L7, that a <see cref="IntegrationMode.Relay"/> connection carrying batch
/// coordinates takes the file path — the agent's file carrier is delivered, its order channel is
/// not — while the SCHEDULED generation kept scanning <c>Mode == Batch</c> alone. The result was
/// the worst available shape: the dispatcher enlisted such a command into a file, the command
/// moved to <c>Batched</c>, and no scheduled pass ever came to DEPOSIT that file — the deposit and
/// the purge belong to the batch job, not to the dispatcher. The write sat in the store, the
/// insurer or the CBS never received it, and the command waited out its whole
/// <c>AckTimeoutHours</c> before anything said so.
/// </para>
///
/// <para>
/// Latent rather than live when it was found: every batch-capable kind today
/// (Perfect Vision, Amplitude, ORASS) has a blocked adapter whose health check cannot pass, so no
/// such connection can be activated and no command can be queued against one. It becomes live on
/// the day a vendor specification arrives — which is exactly the day nobody remembers this.
/// </para>
/// </summary>
internal static class OutboundBatchCarrier
{
    /// <summary>
    /// The real answer, on a loaded connection.
    ///
    /// <para>
    /// <see cref="IntegrationMode.Batch"/> is the mode's own definition. <c>Relay</c> is a
    /// statement about what has been BUILT: <c>RelayFileTransport</c> exists and
    /// <c>IntegrationFileTransportRouter</c> already sends a relay connection's deposits to the
    /// agent, while the order channel of INT-26's platform side does not exist — so a relay
    /// connection with file coordinates belongs on this path, and one without is refused by the
    /// dispatcher rather than called directly from this process.
    /// </para>
    /// </summary>
    internal static bool LeavesInAFile(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.Mode == IntegrationMode.Batch
            || (connection.Mode == IntegrationMode.Relay
                && connection.Settings is BatchCapableSettings);
    }

    /// <summary>
    /// The SQL-translatable <b>superset</b>, for a scan that must narrow in the database before it
    /// can ask the real question.
    ///
    /// <para>
    /// A superset and not the answer: <see cref="LeavesInAFile"/> type-tests the settings, which
    /// are stored as jsonb through a value converter and cannot be type-tested in SQL. So a scan
    /// filters on the mode here and applies <see cref="LeavesInAFile"/> to the loaded rows. That
    /// is sound because the table is per tenant and tiny — a handful of rows — and it keeps the
    /// definition in ONE place, which is the whole point of this type. Writing the mode test
    /// inline at each call site is what produced the drift in the first place.
    /// </para>
    /// </summary>
    internal static bool MayLeaveInAFile(IntegrationMode mode)
        => mode is IntegrationMode.Batch or IntegrationMode.Relay;
}
