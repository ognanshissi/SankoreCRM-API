namespace Sankore.Modules.Customers.Features.Import.ImportFromGoogleSheet;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ImportClientsFromGoogleSheetCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string SpreadsheetUrl,
    Guid? DefaultAgencyId
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientImport";
    public string? ResourceId => null;
}
