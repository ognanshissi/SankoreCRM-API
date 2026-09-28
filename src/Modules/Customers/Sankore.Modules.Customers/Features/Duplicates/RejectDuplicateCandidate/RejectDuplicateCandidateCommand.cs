namespace Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// A reviewer states that the two clients are NOT the same person (US-M01-BE-24).
/// <para>
/// The reason is not stored on the candidate row — the entity only keeps reviewer and date — it is
/// carried by the audit trail, which is why this is an <see cref="ICommand"/> +
/// <see cref="IResourceCommand"/> and why the reason is mandatory.
/// </para>
/// </summary>
public sealed record RejectDuplicateCandidateCommand(Guid CandidateId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DuplicateCandidate";
    public string? ResourceId => CandidateId.ToString();
}
