namespace Sankore.Modules.Leads.Features.Ingestions.ListIngestions;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListIngestionsQuery(
    Guid SourceId,
    LeadIngestionStatus? Status = null,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<IngestionDto>>>;

public sealed record IngestionDto(
    Guid Id,
    DateTimeOffset IngestedAt,
    string? ExternalId,
    LeadIngestionStatus Status,
    string? RejectionReason,
    Guid LeadId);
