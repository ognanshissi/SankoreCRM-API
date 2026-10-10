namespace Sankore.Modules.Customers.Features.Holdings.GetHolding;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Kernel;

/// <summary>One holding with its current beneficiary clause (BNF-B-14, BNF-B-15).</summary>
public sealed record GetHoldingQuery(Guid HoldingId) : IRequest<Result<HoldingDto>>;
