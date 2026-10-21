namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>One template on the administrator's screen (WA-FE-02).</summary>
/// <param name="Status"><c>Approved</c>, <c>Pending</c>, <c>Rejected</c>, <c>Paused</c>, <c>Disabled</c> or <c>Other</c>.</param>
/// <param name="IsAvailable">False once the provider stopped listing it.</param>
public sealed record WhatsAppTemplateDto(
    Guid Id,
    Guid ConnectionId,
    string? ChannelDisplayPhoneNumber,
    string Name,
    string Language,
    string Category,
    string Status,
    bool IsAvailable,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<string> Buttons,
    int VariableCount,
    IReadOnlyList<Guid> ProductLineIds,
    IReadOnlyList<WhatsAppVariableMappingDto> VariableMappings,
    DateTimeOffset SyncedAt);

/// <param name="Field">
/// <c>LeadFirstName</c>, <c>LeadLastName</c>, <c>LeadFullName</c>, <c>InterestedProduct</c>,
/// <c>AgentFirstName</c> or <c>AgentFullName</c>.
/// </param>
public sealed record WhatsAppVariableMappingDto(int Index, WhatsAppTemplateField Field);

public sealed record ConfigureWhatsAppTemplateRequest(
    IReadOnlyList<Guid>? ProductLineIds,
    IReadOnlyList<WhatsAppVariableMappingDto>? VariableMappings);

/// <param name="Channels">How many numbers were read.</param>
/// <param name="Failed">Numbers whose catalogue could not be read, with the reason code.</param>
public sealed record WhatsAppTemplateSyncResult(
    int Channels,
    int Added,
    int Updated,
    int Unavailable,
    IReadOnlyList<string> Failed);

// ── Synchroniser ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Copies each active number's template catalogue into this module (WA-BE-04).
///
/// <para>
/// <b>Upsert by (number, name, language)</b>: the provider's text and status overwrite ours, the
/// institution's product list and variable mapping are kept. A template the provider no longer
/// lists is marked unavailable, not deleted. A number whose catalogue cannot be read is reported
/// and its templates left as they were — an outage must not mark every template unavailable.
/// </para>
/// </summary>
internal sealed class WhatsAppTemplateSynchronizer(
    LeadsDbContext db,
    IIntegrationModule integration,
    TimeProvider clock,
    ILogger<WhatsAppTemplateSynchronizer> logger)
{
    public async Task<WhatsAppTemplateSyncResult> SyncAsync(Guid tenantId, CancellationToken ct)
    {
        var channels = (await integration.Messaging.ListChannelsAsync(tenantId, ct)).Where(c => c.IsActive).ToList();
        var now = clock.GetUtcNow();

        int added = 0, updated = 0, unavailable = 0;
        var failed = new List<string>();

        foreach (var channel in channels)
        {
            var listed = await integration.Messaging.ListTemplatesAsync(tenantId, channel.ConnectionId, ct);

            if (listed.IsFailure)
            {
                logger.LogWarning(
                    "WhatsApp templates of connection {ConnectionId} could not be read: {Code}.",
                    channel.ConnectionId, listed.Code);

                failed.Add($"{channel.DisplayPhoneNumber ?? channel.Name}: {listed.Code}");
                continue;
            }

            var known = await db.WhatsAppTemplates
                .AsTracking()
                .Where(t => t.ConnectionId == channel.ConnectionId)
                .ToListAsync(ct);

            var seen = new HashSet<Guid>();

            foreach (var item in listed.Value)
            {
                var template = known.FirstOrDefault(t =>
                    t.Name == item.Name && string.Equals(t.Language, item.Language, StringComparison.OrdinalIgnoreCase));

                if (template is null)
                {
                    template = WhatsAppTemplate.Discover(tenantId, channel.ConnectionId, item.Name, item.Language, now);
                    db.WhatsAppTemplates.Add(template);
                    known.Add(template);
                    added++;
                }
                else
                {
                    updated++;
                }

                template.Refresh(item.Category, item.Status, item.ProviderTemplateId, item.HeaderText,
                    item.BodyText, item.FooterText, item.Buttons, now);

                seen.Add(template.Id);
            }

            foreach (var gone in known.Where(t => t.IsAvailable && !seen.Contains(t.Id)))
            {
                gone.MarkUnavailable(now);
                unavailable++;
            }
        }

        await db.SaveChangesAsync(ct);

        return new WhatsAppTemplateSyncResult(channels.Count, added, updated, unavailable, failed);
    }
}

/// <summary>
/// The hourly synchronisation of one tenant (WA-BE-04 criterion 1) — registered per tenant at
/// start-up like the module's other recurring jobs, and a no-op for a tenant without an active
/// number. SYSTEM identity: a schedule did this, not a person.
/// </summary>
public sealed class SyncWhatsAppTemplatesJob(IServiceScopeFactory scopeFactory, ILogger<SyncWhatsAppTemplatesJob> logger)
{
    public const string RecurringJobPrefix = "whatsapp-template-sync";
    public const string CronExpression = "7 * * * *";

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var result = await scope.ServiceProvider
            .GetRequiredService<WhatsAppTemplateSynchronizer>()
            .SyncAsync(tenantId, CancellationToken.None);

        if (result.Channels > 0)
        {
            logger.LogInformation(
                "WhatsApp templates of tenant {TenantId}: {Added} added, {Updated} refreshed, {Unavailable} unavailable, {Failed} number(s) unreadable.",
                tenantId, result.Added, result.Updated, result.Unavailable, result.Failed.Count);
        }
    }
}

// ── Slices ───────────────────────────────────────────────────────────────────────────────────

internal sealed record ListWhatsAppTemplatesQuery : IRequest<IReadOnlyList<WhatsAppTemplateDto>>;

internal sealed class ListWhatsAppTemplatesHandler(
    LeadsDbContext db, ICurrentUser currentUser, IIntegrationModule integration)
    : IRequestHandler<ListWhatsAppTemplatesQuery, IReadOnlyList<WhatsAppTemplateDto>>
{
    public async Task<IReadOnlyList<WhatsAppTemplateDto>> Handle(ListWhatsAppTemplatesQuery query, CancellationToken ct)
    {
        var channels = (await integration.Messaging.ListChannelsAsync(currentUser.TenantId, ct))
            .ToDictionary(c => c.ConnectionId);

        var templates = await db.WhatsAppTemplates
            .OrderBy(t => t.Name)
            .ThenBy(t => t.Language)
            .ToListAsync(ct);

        return [.. templates.Select(t => ToDto(t, channels.GetValueOrDefault(t.ConnectionId)?.DisplayPhoneNumber))];
    }

    internal static WhatsAppTemplateDto ToDto(WhatsAppTemplate t, string? displayNumber) => new(
        t.Id, t.ConnectionId, displayNumber, t.Name, t.Language, t.Category, t.Status.ToString(), t.IsAvailable,
        t.HeaderText, t.BodyText, t.FooterText, t.Buttons, t.VariableCount, t.ProductLineIds,
        [.. t.VariableMappings.Select(m => new WhatsAppVariableMappingDto(m.Index, m.Field))], t.SyncedAt);
}

internal sealed record SyncWhatsAppTemplatesCommand : IRequest<WhatsAppTemplateSyncResult>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppTemplate";
    public string? ResourceId => null;
}

/// <summary>« Synchroniser » on the templates screen (WA-BE-04 criterion 2).</summary>
internal sealed class SyncWhatsAppTemplatesHandler(WhatsAppTemplateSynchronizer synchronizer, ICurrentUser currentUser)
    : IRequestHandler<SyncWhatsAppTemplatesCommand, WhatsAppTemplateSyncResult>
{
    public Task<WhatsAppTemplateSyncResult> Handle(SyncWhatsAppTemplatesCommand cmd, CancellationToken ct)
        => synchronizer.SyncAsync(currentUser.TenantId, ct);
}

internal sealed record ConfigureWhatsAppTemplateCommand(Guid TemplateId, ConfigureWhatsAppTemplateRequest Request)
    : IRequest<Result<WhatsAppTemplateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppTemplate";
    public string? ResourceId => TemplateId.ToString();
}

/// <summary>Which products a template is for, and which lead field fills each variable (WA-BE-04 criterion 3).</summary>
internal sealed class ConfigureWhatsAppTemplateHandler(
    LeadsDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IRequestHandler<ConfigureWhatsAppTemplateCommand, Result<WhatsAppTemplateDto>>
{
    public async Task<Result<WhatsAppTemplateDto>> Handle(ConfigureWhatsAppTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.WhatsAppTemplates.AsTracking().FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);
        if (template is null) return Result.Fail<WhatsAppTemplateDto>(WhatsAppErrors.TemplateNotFound);

        var mappings = (cmd.Request.VariableMappings ?? [])
            .Select(m => new WhatsAppVariableMapping(m.Index, m.Field))
            .ToList();

        if (mappings.Any(m => !Enum.IsDefined(m.Field)))
            return Result.Fail<WhatsAppTemplateDto>("WHATSAPP_TEMPLATE_FIELD_UNKNOWN");

        var configured = template.Configure(cmd.Request.ProductLineIds ?? [], mappings, currentUser.Id, clock.GetUtcNow());
        if (configured.IsFailure) return Result.Fail<WhatsAppTemplateDto>(configured.Error!);

        await db.SaveChangesAsync(ct);

        return Result.Ok(ListWhatsAppTemplatesHandler.ToDto(template, null));
    }
}
