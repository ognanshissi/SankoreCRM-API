namespace Sankore.Modules.Leads.Features.CloseLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CloseLeadCommand(
    Guid LeadId,
    LeadCloseReason Reason,
    string? Detail = null,
    /// <summary>UpdatedAt the caller last read; null skips the staleness check.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
