namespace Sankore.Modules.Customers.Features.Import.ImportFromFile;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ImportClientsFromFileCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string FileReference,
    string OriginalFileName,
    Guid? DefaultAgencyId
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientImport";
    public string? ResourceId => null;
}
