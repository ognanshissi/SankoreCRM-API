namespace Sankore.Modules.Leads.Features.Ingestions.ReprocessIngestion;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Completes a failed or rejected ingestion by hand: the operator names which key of the
/// received payload holds each missing lead field, and the lead is created from the stored
/// payload on the same ingestion row. Audited — it reads and acts on a prospect's raw data.
/// </summary>
/// <param name="FieldLinks">Lead target field → top-level key of the received payload.</param>
internal sealed record ReprocessIngestionCommand(
    Guid IngestionId,
    IReadOnlyDictionary<string, string> FieldLinks)
    : IRequest<Result<ReprocessIngestionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadIngestion";
    public string? ResourceId  => IngestionId.ToString();
}

public sealed record ReprocessIngestionRequest(IReadOnlyDictionary<string, string>? FieldLinks);

/// <summary>
/// <c>Accepted</c> with the new lead, or <c>Duplicate</c> with the existing open lead the
/// completed payload matched.
/// </summary>
public sealed record ReprocessIngestionResult(Guid LeadId, LeadIngestionStatus Status);
