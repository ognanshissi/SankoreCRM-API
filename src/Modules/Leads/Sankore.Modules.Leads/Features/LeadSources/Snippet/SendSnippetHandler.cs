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

        // Resolved from the registered SDK version, like the screen's snippet. This handler used
        // to build the mail from SnippetOptions alone, whose default hash is the literal
        // "sha384-placeholder": the snippet we mailed to integrators was refused by every
        // browser it was pasted into.
        var (sdkUrl, sriHash) = await SnippetBuilder.ResolveSdkAsync(db, options.Value, ct);
        var snippet = SnippetBuilder.Build(source, sdkUrl, sriHash);
        var html = snippet.Html;
        var containerId = snippet.ContainerId;

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
                ["sdkUrl"]      = sdkUrl,
                ["sriHash"]     = sriHash,
                ["publicKey"]   = source.PublicKey,
                ["containerId"] = containerId
            },
            IdempotencyKey: $"snippet-{source.Id}-{cmd.RecipientEmail}",
            TenantId:       source.TenantId), ct);

        return Result.Ok();
    }
}
