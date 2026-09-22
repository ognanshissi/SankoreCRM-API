namespace Sankore.Modules.Administration.Features.ImportUsers.ImportFromGoogleContacts;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ImportFromGoogleContactsCommand(
    Guid TenantId,
    Guid InitiatedBy
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "UserImport";
    public string? ResourceId  => null;
}
