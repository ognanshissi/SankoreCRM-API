namespace Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Runs duplicate detection over one tenant (US-M01-BE-24). Dispatched by
/// <see cref="DetectDuplicatesJob"/>, never by an HTTP endpoint.
/// <para>
/// Implements <see cref="ICommand"/> on purpose: the nightly scan writes candidate rows and must be
/// auditable, and it is the SYSTEM account that appears as the actor because the job establishes
/// that identity before sending the command. <see cref="TenantId"/> is an explicit parameter — the
/// job has no HTTP tenant context, and the handler must never guess.
/// </para>
/// </summary>
public sealed record DetectDuplicatesCommand(Guid TenantId)
    : IRequest<Result<DetectDuplicatesResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "DuplicateCandidate";

    /// <summary>A tenant-wide sweep, not a single row.</summary>
    public string? ResourceId => null;
}

/// <summary>Counters of one detection run, logged by the job and asserted by the tests.</summary>
public sealed record DetectDuplicatesResult(
    int ClientsExamined,
    int PairsCompared,
    int CandidatesCreated,
    int CandidatesRefreshed,
    int CandidatesSkipped,
    /// <param name="CandidatesFailed">
    /// Pairs the domain refused, skipped so one bad pair cannot abort a tenant-wide sweep. Non-zero
    /// means a data problem worth investigating — the run completed, but those pairs produced no
    /// candidate; the handler logs each one with both client ids.
    /// </param>
    int CandidatesFailed);

/// <summary>
/// One matched criterion, as persisted in <c>DuplicateCandidate.ReasonsJson</c> and returned to the
/// review screen. Keys and weights only — never a compared value, which would leak the very
/// personal data the detection is careful never to decrypt.
/// </summary>
public sealed record DuplicateReasonDto(string Key, int Weight);
