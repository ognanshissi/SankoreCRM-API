namespace Sankore.Modules.Customers.Features.Holdings.CreateFromOpportunity;

using MediatR;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the DRAFT holding a WON commercial request produces.
///
/// <para>
/// The twin of <see cref="CreateFromLead.CreateHoldingFromLeadCommand"/> and reached the same way:
/// only through <see cref="ICustomersModule.CreateHoldingFromOpportunityAsync"/>, never over HTTP.
/// An agent subscribing a client goes through <c>POST clients/{id}/holdings</c>, which validates
/// the line against the caller's perimeter. This path is M13 saying "the request I just closed as
/// won was about this line", and the actor is the agent who closed it.
/// </para>
///
/// <para>
/// Separate from the lead command rather than one command with two nullable origins: the two ids
/// come from different tables and the audit row has to say which one was presented. A single
/// command would also make "neither" representable at the contract boundary.
/// </para>
/// </summary>
internal sealed record CreateHoldingFromOpportunityCommand(
    Guid TenantId,
    Guid ClientId,
    Guid ProductLineId,
    Guid SourceOpportunityId,
    Guid ClosedByUserId
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public static CreateHoldingFromOpportunityCommand From(
        CreateHoldingFromOpportunityRequest request) => new(
        TenantId:            request.TenantId,
        ClientId:            request.ClientId,
        ProductLineId:       request.ProductLineId,
        SourceOpportunityId: request.SourceOpportunityId,
        ClosedByUserId:      request.ClosedByUserId);

    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
