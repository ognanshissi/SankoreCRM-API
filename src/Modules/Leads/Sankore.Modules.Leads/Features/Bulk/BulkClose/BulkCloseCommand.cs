namespace Sankore.Modules.Leads.Features.Bulk.BulkClose;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record BulkCloseCommand(
    IReadOnlyList<Guid> LeadIds,
    LeadCloseReason Reason,
    string? Detail = null
) : IRequest<Result<BulkOperationResult>>, ICommand;
