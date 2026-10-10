namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetSnippetHandler(
    LeadsDbContext db,
    IOptions<SnippetOptions> options)
    : IRequestHandler<GetSnippetQuery, Result<SnippetResult>>
{
    public async Task<Result<SnippetResult>> Handle(GetSnippetQuery query, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == query.SourceId, ct);

        if (source is null)
            return Result.Fail<SnippetResult>("SOURCE_NOT_FOUND");

        if (source.Mode != IntegrationMode.EmbeddedScript)
            return Result.Fail<SnippetResult>("NOT_EMBEDDED_SCRIPT_MODE");

        if (string.IsNullOrEmpty(source.PublicKey))
            return Result.Fail<SnippetResult>("PUBLIC_KEY_NOT_GENERATED");

        var (sdkUrl, sriHash) = await SnippetBuilder.ResolveSdkAsync(db, options.Value, ct);
        var snippet = SnippetBuilder.Build(source, sdkUrl, sriHash);

        return Result.Ok(new SnippetResult(
            snippet.Html, snippet.SdkUrl, snippet.SriHash, source.PublicKey, snippet.ContainerId));
    }
}

public sealed class SnippetOptions
{
    /// <summary>
    /// Fallback URL when no SDK version is registered — a first-boot case, since the shipped
    /// builds are seeded at start-up. The major alias, not a pinned version, because there is no
    /// version to pin; it is paired with the placeholder hash below and both must be configured
    /// together to be usable.
    /// </summary>
    public string SdkUrl { get; set; } = "/sdk/v1/forms.min.js";

    /// <summary>Fallback SRI hash when no SDK version is published.</summary>
    public string SriHash { get; set; } = "sha384-placeholder";
}
