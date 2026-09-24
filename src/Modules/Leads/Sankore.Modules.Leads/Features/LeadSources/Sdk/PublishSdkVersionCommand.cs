namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record PublishSdkVersionCommand(
    string Version,
    int Major,
    byte[] FileContent
) : IRequest<Result<PublishSdkVersionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "SdkVersion";
    public string? ResourceId  => Version;
}

internal sealed record PublishSdkVersionResult(
    Guid Id,
    string Version,
    string SriHash);
