namespace Sankore.Modules.Leads.Features.CustomerLink.DetachLeadFromCustomer;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Detaches a lead from the client it was attached to — the correction path for a wrong match
/// (BNF-B-22). Always a human decision, so the reason is never optional.
/// </summary>
internal sealed record DetachLeadFromCustomerCommand(
    Guid LeadId,
    string Reason,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
