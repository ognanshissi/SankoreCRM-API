namespace Sankore.Modules.Customers.Features.Holdings.Subscribe;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens a draft subscription for a client (BNF-B-14).
/// </summary>
/// <param name="SourceLeadId">
/// The lead this came from (BNF-B-24). Server-set only — it is absent from the HTTP request for the
/// same reason M13 keeps <c>ProductLineId</c> off its capture contracts: nothing validates it, and
/// a caller able to set it would attribute its own subscription to somebody else's lead.
/// </param>
public sealed record SubscribeToProductCommand(
    Guid ClientId,
    Guid ProductLineId,
    HoldingSpecificsDto? Specifics,
    IReadOnlyList<DesignationInput>? Designations = null,
    Guid? SourceLeadId = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
