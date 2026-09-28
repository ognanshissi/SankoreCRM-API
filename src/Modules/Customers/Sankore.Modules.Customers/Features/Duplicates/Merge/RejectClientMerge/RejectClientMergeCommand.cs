namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Second pair of eyes says no: the merge request is closed without merging anything (US-M01-BE-25).
/// The reason is mandatory — it is the only explanation the requester will get.
/// </summary>
public sealed record RejectClientMergeCommand(Guid MergeRequestId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientMergeRequest";
    public string? ResourceId => MergeRequestId.ToString();
}
