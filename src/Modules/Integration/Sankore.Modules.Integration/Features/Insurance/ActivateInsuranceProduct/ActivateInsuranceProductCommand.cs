namespace Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Puts a product in the catalogue agents can sell from (ASS-03).
///
/// <para>
/// Its own command and its own audit row, like a connection's activation: this is the switch that
/// makes a premium debitable from customers' accounts, and "who turned it on" is the question
/// asked after the fact.
/// </para>
///
/// <para>
/// It does <b>not</b> require the connection to be active. Activating a product is the tenant's
/// statement that the catalogue entry is finished; whether it can be sold TODAY is
/// <c>ProductOfferability</c>'s derived verdict, which also covers the connection, the validity
/// window and the insurer's pricing capability. Refusing here would mean an administrator could
/// not finish configuring a catalogue before its connection went live, and would still leave the
/// product un-offerable the moment the connection was later deactivated — so the check would buy
/// nothing and cost the ordinary workflow.
/// </para>
/// </summary>
internal sealed record ActivateInsuranceProductCommand(Guid ProductId)
    : IRequest<Result<InsuranceProductDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceProduct";

    public string? ResourceId => ProductId.ToString();
}
