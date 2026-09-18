namespace Sankore.Modules.Leads.Features.Bulk.BulkTag;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record BulkTagCommand(
    IReadOnlyList<Guid> LeadIds,
    string Tag,
    Guid AddedBy
) : IRequest<Result<BulkOperationResult>>, ICommand;
