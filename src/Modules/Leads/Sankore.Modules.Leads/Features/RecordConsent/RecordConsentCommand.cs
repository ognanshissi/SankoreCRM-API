namespace Sankore.Modules.Leads.Features.RecordConsent;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record RecordConsentCommand(
    Guid TenantId,
    Guid LeadId,
    ConsentType Type,
    ConsentChannel Channel,
    Guid RecordedBy,
    string? ProofReference = null
) : IRequest<Result<RecordConsentResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record RecordConsentResult(Guid ConsentId, DateTimeOffset GrantedAt);
