namespace Sankore.Modules.Leads.Features.Bulk.BulkAssignOwner;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record BulkAssignOwnerCommand(
    IReadOnlyList<Guid> LeadIds,
    Guid OwnerId
) : IRequest<Result<BulkOperationResult>>, ICommand;
