namespace Sankore.Modules.Leads.Features.Ingestion;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Unified ingestion entry point (US-F13.37-BE-10).
/// All modes (webhook, pull, platform, internal) flow through this command.
/// </summary>
internal sealed record IngestInboundLeadCommand(
    Guid TenantId,
    Guid SourceId,
    string RawPayloadJson,
    string? ExternalId = null,
    Guid? RunId = null
) : IRequest<Result<IngestInboundLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadIngestion";
    public string? ResourceId  => null;
}

/// <param name="Updated">
/// True when this push carried an external id already received and, the source allowing it,
/// updated that lead's custom fields (webhooks lot 5). <see cref="Status"/> is then the original
/// ingestion's.
/// </param>
internal sealed record IngestInboundLeadResult(
    Guid IngestionId,
    Guid? LeadId,
    LeadIngestionStatus Status,
    string? Error = null,
    bool Updated = false);
