namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Converts a lead into a customer.
/// If <see cref="CustomerId"/> is null the handler generates a new Guid that
/// the Customers module will use as the customer's Id when it processes the
/// <c>LeadConvertedIntegrationEvent</c> from the outbox.
/// </summary>
internal sealed record ConvertLeadCommand(
    Guid LeadId,
    Guid? CustomerId = null
) : IRequest<Result<ConvertLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record ConvertLeadResult(
    Guid LeadId,
    Guid CustomerId,
    DateTimeOffset ConvertedAt);
