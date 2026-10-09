namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The real <see cref="IBatchFileEnlister"/> — what the dispatcher calls when the connection is in
/// <see cref="IntegrationMode.Batch"/> (INT-24, criterion 1).
///
/// <para>
/// It replaces <c>UnavailableBatchFileEnlister</c>, which exists only to say that a deployment has
/// no outbound writer. Registration order therefore matters: see
/// <c>OutboundBatchServiceRegistration</c>.
/// </para>
///
/// <para>
/// <b>It calls no adapter and no port, and that is the criterion.</b> A batch connection has no
/// endpoint to call — the write leaves in a file — so the dispatcher routes here instead of
/// resolving an adapter at all (<c>ExecuteIntegrationCommandHandler</c> branches on the mode
/// before it reaches <c>ResolveAdapter</c>), and this type holds no reference to a port. The
/// absence is the property, which is why the test asserts on a recording adapter's empty call
/// list rather than on anything this class returns.
/// </para>
///
/// <para>
/// <b>Why it generates rather than looking up.</b> A file's checksum and record count are fixed
/// at creation — <c>IntegrationBatchFile</c> has no mutator and must not grow one — so there is
/// no such thing as an open file a late command can be appended to. Attaching a command to an
/// already-written file would mark it <c>Batched</c> against content that does not mention it,
/// and it would then wait for an acknowledgement that can never arrive. So the command either
/// goes into a file produced now, with it inside, or it waits for the next cut-off. The generator
/// is handed this command as its seed and leaves its status move to the handler.
/// </para>
///
/// <para>
/// The scheduled job is the ordinary driver (criterion 2); this path is what covers a command
/// whose cut-off has passed while the job had not yet run — a paused schedule, a restart, a
/// command that was still in backoff at the cut-off. One generator, two drivers, and no overlap:
/// every generation renders its own content from its own eligible set and moves those commands in
/// the same transaction.
/// </para>
/// </summary>
internal sealed class OutboundBatchFileEnlister(
    OutboundBatchFileGenerator generator,
    ILogger<OutboundBatchFileEnlister> logger) : IBatchFileEnlister
{
    public async Task<IntegrationResult<Guid>> EnlistAsync(
        IntegrationCommand command, IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(connection);

        var generated = await generator.GenerateAsync(
            command.TenantId, connection, seedCommandId: command.Id, ct);

        if (generated.IsFailure) return Relabel(generated);

        if (!generated.Value.Generated)
        {
            // The generator found nothing eligible even though this command was offered as the
            // seed. Structurally unreachable — a non-eligible seed is reported as a failure above
            // — so it is an invariant breach rather than a state anybody can act on, and it must
            // not be answered with a file id that does not exist.
            logger.LogError(
                "The batch generator produced no file for command {CommandId} of connection "
                + "{ConnectionId} although the command was its seed.",
                command.Id, connection.Id);

            return IntegrationResult.Transient<Guid>(
                IntegrationErrors.Unavailable,
                "No outbound file was produced for this command; it stays queued.");
        }

        logger.LogInformation(
            "Command {CommandId} left in outbound batch file {FileId} (sequence {SequenceNo}) "
            + "without any call to the external system.",
            command.Id, generated.Value.FileId, generated.Value.SequenceNo);

        return IntegrationResult.Ok(generated.Value.FileId);
    }

    /// <summary>
    /// The generator's failure, re-shaped to carry a <see cref="Guid"/> and <b>keeping its
    /// family</b>.
    ///
    /// <para>
    /// The family is what <c>ExecuteIntegrationCommandHandler.ApplyFailureAsync</c> reads to
    /// decide between a retry and a rejection, so flattening it would be the difference between a
    /// command that waits for the next cut-off and one parked in the rejection queue. The two
    /// cases this actually carries: <c>Transient</c> for "the cut-off has not come", and
    /// <c>Technical</c> for a misconfigured encoding or a missing storage key, which no retry can
    /// fix.
    /// </para>
    /// </summary>
    private static IntegrationResult<Guid> Relabel(IntegrationResult failure)
        => failure.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional<Guid>(failure.Code!, failure.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical<Guid>(failure.Code!, failure.Detail),
            _ => IntegrationResult.Transient<Guid>(failure.Code!, failure.Detail),
        };
}
