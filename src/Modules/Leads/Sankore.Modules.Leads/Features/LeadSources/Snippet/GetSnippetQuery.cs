namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using MediatR;
using Sankore.Shared.Kernel;

/// <param name="RequestHost">Host the CRM called, the API address when none is configured.</param>
internal sealed record GetSnippetQuery(Guid SourceId, string? RequestHost = null)
    : IRequest<Result<SnippetResult>>;

internal sealed record SnippetResult(
    string Html,
    string SdkUrl,
    string SriHash,
    string PublicKey,
    string? FormContainerId,
    // Absolute API address the snippet and the Angular/React code post to (no trailing slash).
    string ApiBaseUrl = "");
