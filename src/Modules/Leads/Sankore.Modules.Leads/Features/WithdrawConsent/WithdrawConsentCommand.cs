namespace Sankore.Modules.Leads.Features.WithdrawConsent;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record WithdrawConsentCommand(
    Guid LeadId,
    Guid ConsentId,
    Guid WithdrawnBy,
    string? Reason = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
