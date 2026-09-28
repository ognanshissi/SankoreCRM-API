namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Second pair of eyes: approves a pending merge request and executes it synchronously
/// (US-M01-BE-25). <see cref="ICommand"/> is what puts the whole execution — reassignments,
/// status change and outbox rows — inside one transaction and one audit entry.
/// </summary>
public sealed record ApproveClientMergeCommand(Guid MergeRequestId, string? Comment)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientMergeRequest";
    public string? ResourceId => MergeRequestId.ToString();
}
