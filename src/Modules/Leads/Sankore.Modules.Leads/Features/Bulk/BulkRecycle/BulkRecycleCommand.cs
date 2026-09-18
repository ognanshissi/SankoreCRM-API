namespace Sankore.Modules.Leads.Features.Bulk.BulkRecycle;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record BulkRecycleCommand(
    IReadOnlyList<Guid> LeadIds,
    LeadSource? NewSource = null,
    string? NewCampaign = null
) : IRequest<Result<BulkOperationResult>>, ICommand;
