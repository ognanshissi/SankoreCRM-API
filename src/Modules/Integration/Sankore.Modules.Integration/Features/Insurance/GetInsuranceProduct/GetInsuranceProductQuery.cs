namespace Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>One catalogue entry, with its guarantees and its offerability verdict (ASS-03).</summary>
internal sealed record GetInsuranceProductQuery(Guid ProductId)
    : IRequest<Result<InsuranceProductDto>>;
