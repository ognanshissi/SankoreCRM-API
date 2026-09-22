namespace Sankore.Modules.Administration.Features.ImportUsers.ImportFromGoogleSheet;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ImportFromGoogleSheetCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string SpreadsheetUrl
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "UserImport";
    public string? ResourceId  => null;
}
