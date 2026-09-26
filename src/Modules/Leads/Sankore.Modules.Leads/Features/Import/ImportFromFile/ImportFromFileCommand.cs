namespace Sankore.Modules.Leads.Features.Import.ImportFromFile;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Thin command — schedules a lead-import Hangfire job for an uploaded CSV/Excel file.
/// The payload carries only the opaque file reference, never the raw data.
/// Each row is later processed through <c>CaptureLeadCommand</c>.
/// </summary>
public sealed record ImportFromFileCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string FileReference,
    string OriginalFileName,
    ImportDefaults? Defaults = null
) : IRequest<Result<ImportLeadsAccepted>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadImport";
    public string? ResourceId => null;
}
