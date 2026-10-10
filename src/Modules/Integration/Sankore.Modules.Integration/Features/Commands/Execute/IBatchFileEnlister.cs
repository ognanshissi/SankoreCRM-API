namespace Sankore.Modules.Integration.Features.Commands.Execute;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Puts a command into the outbound file of a <see cref="IntegrationMode.Batch"/> connection and
/// answers which file it joined (INT-24).
///
/// <para>
/// The seam exists because the two halves belong to different stories and must not be welded
/// together: the STATUS MOVE is INT-05's transition table (<c>Sending → Batched</c>, then closed
/// later by an acknowledgement), while writing the file, numbering its sequence and depositing it
/// over SFTP is INT-24's. The dispatcher below therefore never calls an adapter for a batch
/// connection — a batch CBS has no endpoint to call — and never invents a file either.
/// </para>
/// </summary>
internal interface IBatchFileEnlister
{
    Task<IntegrationResult<Guid>> EnlistAsync(
        IntegrationCommand command, IntegrationConnection connection, CancellationToken ct);
}

/// <summary>
/// What a deployment without the batch socle answers: a TRANSIENT failure naming what is missing.
///
/// <para>
/// Transient and not Technical, which is a deliberate reading of the three families. The command
/// is perfectly valid and its configuration is right — the file writer is simply not there yet,
/// so the command waits and leaves with the first file that is generated. A Technical family
/// would park it in the rejection queue and wake an administrator for something they cannot fix,
/// and a Functional one would claim the external system refused it. It still ends up rejected
/// after <see cref="ICommandRetryPolicy.MaxAttempts"/>, which is the honest outcome if the socle
/// never arrives: nothing is silently dropped, and nothing claims to have been sent.
/// </para>
///
/// <para>
/// Registered with <c>TryAdd</c> by <c>AddCommandsServices</c>, so INT-24's writer — registered
/// by the module before that call — takes precedence over this one.
/// </para>
/// </summary>
internal sealed class UnavailableBatchFileEnlister : IBatchFileEnlister
{
    public Task<IntegrationResult<Guid>> EnlistAsync(
        IntegrationCommand command, IntegrationConnection connection, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Transient<Guid>(
            IntegrationErrors.Unavailable,
            "No outbound batch writer is registered in this deployment (INT-24); the command "
            + "stays queued and will leave with the first generated file."));
}
