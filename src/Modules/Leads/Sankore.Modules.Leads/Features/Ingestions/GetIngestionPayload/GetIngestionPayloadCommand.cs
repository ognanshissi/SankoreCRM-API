namespace Sankore.Modules.Leads.Features.Ingestions.GetIngestionPayload;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// ICommand (not a query) because reading the payload is an auditable action
/// — sensitive raw data access must appear in the audit trail.
/// </summary>
internal sealed record GetIngestionPayloadCommand(Guid IngestionId)
    : IRequest<Result<IngestionPayloadDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadIngestion";
    public string? ResourceId  => IngestionId.ToString();
}

public sealed record IngestionPayloadDto(
    Guid Id,
    string? RawPayloadJson);
