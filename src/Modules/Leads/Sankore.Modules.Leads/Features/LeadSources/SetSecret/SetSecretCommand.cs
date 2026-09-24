namespace Sankore.Modules.Leads.Features.LeadSources.SetSecret;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record SetSecretCommand(
    Guid SourceId,
    string Name,
    [property: SensitiveData] string Value,
    DateTimeOffset? ExpiresAt = null
) : IRequest<Result<SecretHint>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceSecret";
    public string? ResourceId  => $"{SourceId}:{Name}";
}
