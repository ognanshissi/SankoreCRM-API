namespace Sankore.Modules.Customers.Features.Holdings.CreateFromOpportunity;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// A won request with a resolved product line leaves a draft holding behind.
/// </summary>
/// <remarks>
/// The whole body lives in <see cref="DraftHoldingOpener"/>, shared with the lead-conversion path.
/// </remarks>
internal sealed class CreateHoldingFromOpportunityHandler(DraftHoldingOpener opener)
    : IRequestHandler<CreateHoldingFromOpportunityCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(
        CreateHoldingFromOpportunityCommand cmd, CancellationToken ct)
        => opener.OpenAsync(
            DraftHoldingOrigin.FromOpportunity(
                tenantId:            cmd.TenantId,
                clientId:            cmd.ClientId,
                productLineId:       cmd.ProductLineId,
                sourceOpportunityId: cmd.SourceOpportunityId,
                openedByUserId:      cmd.ClosedByUserId),
            ct);
}
