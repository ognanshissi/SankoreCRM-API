namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetSnippetQuery(Guid SourceId)
    : IRequest<Result<SnippetResult>>;

internal sealed record SnippetResult(
    string Html,
    string SdkUrl,
    string SriHash,
    string PublicKey,
    string? FormContainerId);
