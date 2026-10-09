namespace Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Edits one catalogue entry (ASS-03).
///
/// <para>
/// The insurer and the insurer's product code are deliberately absent: they are the product's
/// identity, and changing either would re-point every subscription, policy and statement line
/// that names it at a different contract at a different insurer. The same reasoning that keeps
/// <c>ConnectionId</c> and <c>Kind</c> out of <c>UpdateConnectionCommand</c>.
/// </para>
///
/// <para>
/// <c>IsActive</c> is absent too: activation is its own route under its own audit row, like a
/// connection's, because it is the switch that makes the product sellable.
/// </para>
/// </summary>
internal sealed record UpdateInsuranceProductCommand(
    Guid ProductId,
    InsuranceProductWriteRequest Body)
    : IRequest<Result<InsuranceProductDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceProduct";

    public string? ResourceId => ProductId.ToString();
}
