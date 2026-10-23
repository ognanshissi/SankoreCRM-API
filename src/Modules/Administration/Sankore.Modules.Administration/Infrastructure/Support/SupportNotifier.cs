namespace Sankore.Modules.Administration.Infrastructure.Support;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Features.Support;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>Who hears about support tickets. Bound from <c>Support:Notifications</c>.</summary>
public sealed class SupportNotificationOptions
{
    public const string SectionName = "Support:Notifications";

    /// <summary>
    /// The platform team's single address (product owner, 2026-10-23): told when a ticket is
    /// opened and when a tenant answers. Empty = nobody is mailed, and each skipped mail is logged
    /// — the queue in the admin app still shows everything.
    /// </summary>
    public string? PlatformRecipient { get; set; }

    /// <summary>The admin app's address, for the link in the platform's e-mails. Optional.</summary>
    public string? AdminAppUrl { get; set; }
}

/// <summary>
/// The support e-mails. Two audiences, two moments:
/// <list type="bullet">
///   <item>the PLATFORM, when a ticket is opened or the tenant answers — sent by the handler that
///   relayed the act, right after the registry accepted it;</item>
///   <item>the REQUESTER, when the platform answers or changes the status — that happens in the
///   registry, which sends nothing, so <c>NotifySupportActivityJob</c> finds it.</item>
/// </list>
/// Never throws: a mail that cannot be queued is logged, the act it reports stands.
/// </summary>
internal sealed class SupportNotifier(
    INotificationsModule notifications,
    ITenantStore tenantStore,
    AdministrationDbContext db,
    IOptions<SupportNotificationOptions> options,
    ILogger<SupportNotifier> logger)
{
    public const string OpenedTemplate = "support-ticket.opened";
    public const string TenantRepliedTemplate = "support-ticket.tenant-replied";
    public const string RepliedTemplate = "support-ticket.replied";
    public const string StatusChangedTemplate = "support-ticket.status-changed";

    /// <summary>The longest part of a message an e-mail quotes; the rest is read in the app.</summary>
    internal const int ExcerptLength = 600;

    public static string TicketPath(Guid ticketId) => $"/support/{ticketId}";

    public static string StatusLabel(SupportTicketStatus status) => status switch
    {
        SupportTicketStatus.New => "Nouveau",
        SupportTicketStatus.InProgress => "En cours",
        SupportTicketStatus.WaitingOnTenant => "En attente de votre réponse",
        SupportTicketStatus.Resolved => "Résolu",
        SupportTicketStatus.Closed => "Fermé",
        _ => status.ToString(),
    };

    public static string CategoryLabel(SupportTicketCategory category) => category switch
    {
        SupportTicketCategory.Bug => "Anomalie",
        SupportTicketCategory.Question => "Question",
        SupportTicketCategory.Access => "Accès / compte",
        SupportTicketCategory.ModulesBilling => "Modules / facturation",
        SupportTicketCategory.Suggestion => "Suggestion",
        _ => category.ToString(),
    };

    public static string PriorityLabel(SupportTicketPriority priority) => priority switch
    {
        SupportTicketPriority.Low => "Basse",
        SupportTicketPriority.Normal => "Normale",
        SupportTicketPriority.High => "Haute",
        SupportTicketPriority.Blocking => "Bloquante",
        _ => priority.ToString(),
    };

    internal static string Excerpt(string text) =>
        text.Length <= ExcerptLength ? text : text[..ExcerptLength].TrimEnd() + " …";

    public Task<bool> OpenedAsync(Guid tenantId, Guid ticketId, string reference, string subject,
        SupportTicketCategory category, SupportTicketPriority priority, SupportCaller requester, string body,
        CancellationToken ct) =>
        ToPlatformAsync(tenantId, OpenedTemplate, $"{OpenedTemplate}:{ticketId}", ticketId,
            new Dictionary<string, object>
            {
                ["reference"] = reference,
                ["subject"] = subject,
                ["category"] = CategoryLabel(category),
                ["priority"] = PriorityLabel(priority),
                ["requested_by"] = $"{requester.FullName} <{requester.Email}>",
                ["excerpt"] = Excerpt(body),
            }, ct);

    public Task<bool> TenantRepliedAsync(Guid tenantId, Guid ticketId, string reference, string subject,
        string author, string body, DateTimeOffset at, CancellationToken ct) =>
        ToPlatformAsync(tenantId, TenantRepliedTemplate, $"{TenantRepliedTemplate}:{ticketId}:{at.UtcTicks}", ticketId,
            new Dictionary<string, object>
            {
                ["reference"] = reference,
                ["subject"] = subject,
                ["author"] = author,
                ["excerpt"] = Excerpt(body),
            }, ct);

    /// <summary>
    /// Tells the requester what the platform did, by e-mail and in the bell. Safe to call on every
    /// run of the job: both channels drop a key they have already seen.
    /// </summary>
    /// <param name="reply">The answer, when the platform wrote one; null for a bare status change.</param>
    /// <param name="newStatus">The status reached, when it moved.</param>
    public async Task<bool> PlatformActedAsync(Guid tenantId, Guid key, Guid ticketId, string reference, string subject,
        Guid requesterUserId, string requesterEmail, string authorName, string? reply, SupportTicketStatus? newStatus,
        CancellationToken ct)
    {
        try
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            var requester = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.TenantId == tenantId && u.Id == requesterUserId)
                .Select(u => new { u.Id, u.FullName, u.PreferredLanguage, u.Status })
                .FirstOrDefaultAsync(ct);

            // A disabled account keeps a mailbox the provider accepts; nobody there should read this.
            if (requester is { Status: Domain.UserStatus.Disabled }) return true;

            var isReply = reply is not null;
            var template = isReply ? RepliedTemplate : StatusChangedTemplate;
            var statusLabel = newStatus is { } s ? StatusLabel(s) : string.Empty;

            await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: template,
                RecipientEmail: requesterEmail,
                RecipientName: requester?.FullName,
                Module: "Administration",
                Locale: requester?.PreferredLanguage ?? tenant?.DefaultLanguage ?? "fr",
                TemplateData: new Dictionary<string, object>
                {
                    ["full_name"] = requester?.FullName ?? string.Empty,
                    ["company_name"] = tenant?.Name ?? string.Empty,
                    ["reference"] = reference,
                    ["subject"] = subject,
                    ["author"] = authorName,
                    ["excerpt"] = reply is null ? string.Empty : Excerpt(reply),
                    ["status_label"] = statusLabel,
                    ["ticket_url"] = TicketUrl(tenant?.Fqdn, ticketId),
                },
                IdempotencyKey: $"support-ticket.activity:{key}",
                TenantId: tenantId), ct);

            if (requester is not null)
            {
                await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                    TenantId: tenantId,
                    UserId: requester.Id,
                    Category: "support.activity",
                    Title: isReply ? $"Réponse du support — {reference}" : $"{reference} : {statusLabel}",
                    Body: subject,
                    Link: TicketPath(ticketId),
                    IdempotencyKey: $"support-ticket.activity:{key}"), ct);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not notify support activity {Key} on ticket {TicketId} (tenant {TenantId})",
                key, ticketId, tenantId);
            return false;
        }
    }

    private async Task<bool> ToPlatformAsync(Guid tenantId, string template, string key, Guid ticketId,
        Dictionary<string, object> data, CancellationToken ct)
    {
        var recipient = options.Value.PlatformRecipient?.Trim();
        if (string.IsNullOrEmpty(recipient))
        {
            logger.LogWarning("No {Section}:PlatformRecipient configured: '{Template}' for tenant {TenantId} not mailed",
                SupportNotificationOptions.SectionName, template, tenantId);
            return false;
        }

        try
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            data["company_name"] = tenant?.Name ?? string.Empty;
            data["full_name"] = string.Empty;
            data["admin_url"] = options.Value.AdminAppUrl is { Length: > 0 } admin
                ? $"{admin.TrimEnd('/')}/support/{ticketId}"
                : string.Empty;

            await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: template,
                RecipientEmail: recipient,
                RecipientName: null,
                Module: "Administration",
                // The platform team reads French; the tenant's own language does not apply.
                Locale: "fr",
                TemplateData: data,
                IdempotencyKey: key,
                TenantId: tenantId), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue '{Template}' for tenant {TenantId}; the act itself stands",
                template, tenantId);
            return false;
        }
    }

    private static string TicketUrl(string? fqdn, Guid ticketId) =>
        string.IsNullOrWhiteSpace(fqdn) ? TicketPath(ticketId) : $"{fqdn.TrimEnd('/')}{TicketPath(ticketId)}";
}
