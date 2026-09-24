namespace Sankore.Modules.Leads.Features.LeadSources.RotateHmacSecret;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record RotateHmacSecretCommand(Guid SourceId)
    : IRequest<Result<RotateHmacSecretResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceSecret";
    public string? ResourceId  => $"{SourceId}:hmac";
}

internal sealed record RotateHmacSecretResult(
    [property: SensitiveData] string Secret,
    DateTimeOffset OldExpiresAt);
