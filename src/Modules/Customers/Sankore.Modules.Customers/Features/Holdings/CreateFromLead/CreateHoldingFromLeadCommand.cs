namespace Sankore.Modules.Customers.Features.Holdings.CreateFromLead;

using MediatR;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the DRAFT holding a converted lead produces (BNF-B-24).
///
/// <para>
/// Reached only through <see cref="ICustomersModule.CreateHoldingFromLeadAsync"/>, never over HTTP:
/// an agent subscribing a client goes through <c>POST clients/{id}/holdings</c>, which validates
/// the line against the caller's perimeter. This path is M13 saying "the lead I just converted was
/// about this line", and the actor is the agent who converted it.
/// </para>
/// </summary>
internal sealed record CreateHoldingFromLeadCommand(
    Guid TenantId,
    Guid ClientId,
    Guid ProductLineId,
    Guid SourceLeadId,
    Guid ConvertedByUserId
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public static CreateHoldingFromLeadCommand From(CreateHoldingFromLeadRequest request) => new(
        TenantId:           request.TenantId,
        ClientId:           request.ClientId,
        ProductLineId:      request.ProductLineId,
        SourceLeadId:       request.SourceLeadId,
        ConvertedByUserId:  request.ConvertedByUserId);

    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
