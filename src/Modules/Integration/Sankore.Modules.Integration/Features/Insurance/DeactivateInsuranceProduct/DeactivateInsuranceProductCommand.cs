namespace Sankore.Modules.Integration.Features.Insurance.DeactivateInsuranceProduct;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Withdraws a product from the catalogue (ASS-03).
///
/// <para>
/// <b>There is no delete endpoint, and this is it.</b> Subscriptions, policies, premium
/// instalments and a year of statement lines name a product, and a deleted row would make all of
/// them unreadable — the same reason a connection is deactivated and never deleted, and the same
/// rule M01 follows with its clients (« archiving is a status »). The database agrees: every
/// foreign key pointing here is <c>Restrict</c>.
/// </para>
///
/// <para>
/// Existing policies keep running. A withdrawal stops NEW subscriptions; cancelling live
/// contracts is <c>CancelPolicy</c>, a different act with a different consequence for the
/// customer.
/// </para>
/// </summary>
internal sealed record DeactivateInsuranceProductCommand(Guid ProductId)
    : IRequest<Result<InsuranceProductDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceProduct";

    public string? ResourceId => ProductId.ToString();
}
