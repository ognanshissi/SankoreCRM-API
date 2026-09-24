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

        var settings = source.Settings as EmbeddedScriptSettings;
        var containerId = settings?.FormContainerId ?? "sankore-form";
        var sdkUrl = options.Value.SdkUrl;
        var sriHash = options.Value.SriHash;

        var html = $"""
            <!-- Sankore CRM Lead Capture — {source.Label} -->
            <div id="{containerId}"></div>
            <script src="{sdkUrl}"
                    integrity="{sriHash}"
                    crossorigin="anonymous"
                    data-key="{source.PublicKey}"
                    data-container="#{containerId}"
                    defer></script>
            """;

        return Result.Ok(new SnippetResult(html, sdkUrl, sriHash, source.PublicKey, containerId));
    }
}

public sealed class SnippetOptions
{
    /// <summary>CDN URL for the current SDK version.</summary>
    public string SdkUrl { get; set; } = "https://cdn.sankore.io/sdk/v1/sankore-forms.min.js";

    /// <summary>SRI hash (sha384) for the SDK file.</summary>
    public string SriHash { get; set; } = "sha384-placeholder";
}
