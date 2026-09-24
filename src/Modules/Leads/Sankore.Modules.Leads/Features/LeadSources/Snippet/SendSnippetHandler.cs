namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class SendSnippetHandler(
    LeadsDbContext db,
    INotificationsModule notifications,
    IOptions<SnippetOptions> options)
    : IRequestHandler<SendSnippetCommand, Result>
{
    public async Task<Result> Handle(SendSnippetCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("SOURCE_NOT_FOUND");

        if (source.Mode != IntegrationMode.EmbeddedScript)
            return Result.Fail("NOT_EMBEDDED_SCRIPT_MODE");

        if (string.IsNullOrEmpty(source.PublicKey))
            return Result.Fail("PUBLIC_KEY_NOT_GENERATED");

        var settings = source.Settings as EmbeddedScriptSettings;
        var containerId = settings?.FormContainerId ?? "sankore-form";

        var html = $"""
            <!-- Sankore CRM Lead Capture — {source.Label} -->
            <div id="{containerId}"></div>
            <script src="{options.Value.SdkUrl}"
                    integrity="{options.Value.SriHash}"
                    crossorigin="anonymous"
                    data-key="{source.PublicKey}"
                    data-container="#{containerId}"
                    defer></script>
            """;

        await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey:    "lead-source.snippet",
            RecipientEmail: cmd.RecipientEmail,
            RecipientName:  null,
            Module:         "Leads",
            Locale:         "fr",
            TemplateData: new Dictionary<string, object>
            {
                ["sourceLabel"] = source.Label,
                ["snippet"]     = html,
                ["sdkUrl"]      = options.Value.SdkUrl,
                ["sriHash"]     = options.Value.SriHash,
                ["publicKey"]   = source.PublicKey,
                ["containerId"] = containerId
            },
            IdempotencyKey: $"snippet-{source.Id}-{cmd.RecipientEmail}",
            TenantId:       source.TenantId), ct);

        return Result.Ok();
    }
}
