namespace Sankore.Modules.Administration.Features.ImportUsers.ImportFromFile;

using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ImportFromFileCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string FileReference,
    string OriginalFileName
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "UserImport";
    public string? ResourceId  => null;
}
