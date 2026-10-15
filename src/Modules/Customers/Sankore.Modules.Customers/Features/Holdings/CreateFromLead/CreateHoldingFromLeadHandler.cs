namespace Sankore.Modules.Customers.Features.Holdings.CreateFromLead;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// BNF-B-24: a converted lead with a resolved product line leaves a draft holding behind.
/// </summary>
/// <remarks>
/// The whole body lives in <see cref="DraftHoldingOpener"/>, shared with the won-request path:
/// three of its four gates — the client exists, the line resolves, <c>AllowMultipleHoldings</c>
/// permits a second contract — say nothing about where the outcome came from, and keeping two
/// copies of them was two chances to get the last one wrong.
/// </remarks>
internal sealed class CreateHoldingFromLeadHandler(DraftHoldingOpener opener)
    : IRequestHandler<CreateHoldingFromLeadCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(CreateHoldingFromLeadCommand cmd, CancellationToken ct)
        => opener.OpenAsync(
            DraftHoldingOrigin.FromLead(
                tenantId:       cmd.TenantId,
                clientId:       cmd.ClientId,
                productLineId:  cmd.ProductLineId,
                sourceLeadId:   cmd.SourceLeadId,
                openedByUserId: cmd.ConvertedByUserId),
            ct);
}
