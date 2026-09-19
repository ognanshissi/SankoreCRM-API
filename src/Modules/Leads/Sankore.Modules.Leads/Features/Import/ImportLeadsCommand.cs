namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Thin command — schedules a lead-import Hangfire job.
/// The payload carries only the opaque file reference, never the raw data.
/// Each row is later processed through <see cref="CaptureLead.CaptureLeadCommand"/>.
/// </summary>
public sealed record ImportLeadsCommand(
    Guid TenantId,
    Guid InitiatedBy,
    string FileReference,
    string OriginalFileName
) : IRequest<Result<ImportLeadsAccepted>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadImport";
    public string? ResourceId => null;
}

public sealed record ImportLeadsAccepted(Guid ImportJobId);
