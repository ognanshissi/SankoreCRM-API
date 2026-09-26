namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleSheet;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public sealed record ImportFromGoogleSheetCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string SpreadsheetUrl,
    ImportDefaults? Defaults = null
) : IRequest<Result<ImportLeadsAccepted>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadImport";
    public string? ResourceId => null;
}
