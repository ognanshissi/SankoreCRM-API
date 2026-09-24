namespace Sankore.Modules.Leads.Features.Ingestion.Pull.DryRun;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DryRunCommand(Guid SourceId)
    : IRequest<Result<DryRunResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}

public sealed record DryRunResult(
    DryRunRequestInfo Request,
    string? RawResponseTruncated,
    IReadOnlyList<DryRunLeadPreview> SimulatedLeads,
    int TotalFetched,
    int MappingErrors);

public sealed record DryRunRequestInfo(
    string Url,
    string Method,
    string? AuthType);

public sealed record DryRunLeadPreview(
    string? FullName,
    string? PhoneNumber,
    string? Email,
    string? ExternalId,
    string? Error);
