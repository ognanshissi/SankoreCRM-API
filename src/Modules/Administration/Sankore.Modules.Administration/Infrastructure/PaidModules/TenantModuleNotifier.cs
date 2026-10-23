namespace Sankore.Modules.Administration.Infrastructure.PaidModules;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Who hears about a paid module. Bound from <c>TenantModules:Notifications</c>.
/// </summary>
public sealed class TenantModuleNotificationOptions
{
    public const string SectionName = "TenantModules:Notifications";

    /// <summary>
    /// The platform team's addresses: told when a tenant asks for a module (they must decide) and
    /// when one deactivates a module (it changes the invoice). Empty = nobody is mailed, and each
    /// skipped mail is logged — the registry's queue in the admin app still shows everything.
    /// </summary>
    public string[] PlatformRecipients { get; set; } = [];
}

/// <summary>
/// The paid-module e-mails (lot 4). Two audiences, two moments:
/// <list type="bullet">
///   <item>the PLATFORM team, when a tenant asks for a module or deactivates one — sent by the
///   handler that relayed the act, right after the registry accepted it;</item>
///   <item>the REQUESTER, when the platform authorised or refused — decisions are taken in the
///   registry, which sends nothing, so <c>NotifyTenantModuleDecisionsJob</c> finds them.</item>
/// </list>
/// Never throws: a mail that cannot be queued is logged, the act it reports stands. Idempotency
/// keys are global in M08's outbox, hence the subscription or tenant id in every key.
/// </summary>
internal sealed class TenantModuleNotifier(
    INotificationsModule notifications,
    ITenantStore tenantStore,
    AdministrationDbContext db,
    IOptions<TenantModuleNotificationOptions> options,
    ILogger<TenantModuleNotifier> logger)
{
    public const string RequestedTemplate = "tenant-module.requested";
    public const string DeactivatedTemplate = "tenant-module.deactivated";
    public const string ApprovedTemplate = "tenant-module.approved";
    public const string RejectedTemplate = "tenant-module.rejected";

    private const string ModulesPath = "/settings/modules";

    /// <summary>The platform's catalogue may rename a module; the mails use the codes' usual names.</summary>
    public static string Label(string module) => module switch
    {
        TenantModules.BankingMicrofinance => "Banking - Microfinance",
        TenantModules.Insurance => "Assurance",
        _ => module,
    };

    public static string Price(decimal amount, string currency) =>
        $"{amount.ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))} {currency}".Trim();

    public Task<int> RequestedAsync(Guid tenantId, Guid subscriptionId, string module, string requestedBy,
        decimal acceptedPrice, string currency, CancellationToken ct) =>
        ToPlatformAsync(tenantId, RequestedTemplate, $"{RequestedTemplate}:{subscriptionId}",
            new Dictionary<string, object>
            {
                ["module_label"] = Label(module),
                ["requested_by"] = requestedBy,
                ["accepted_price"] = Price(acceptedPrice, currency),
            }, ct);

    public Task<int> DeactivatedAsync(Guid tenantId, string module, string deactivatedBy, DateTimeOffset at,
        CancellationToken ct) =>
        ToPlatformAsync(tenantId, DeactivatedTemplate,
            $"{DeactivatedTemplate}:{tenantId}:{module}:{at.UtcTicks}",
            new Dictionary<string, object>
            {
                ["module_label"] = Label(module),
                ["deactivated_by"] = deactivatedBy,
            }, ct);

    /// <summary>
    /// Tells the requester the platform's answer, by e-mail and in the bell. Safe to call on every
    /// run of the job: both channels drop a key they have already seen.
    /// </summary>
    public async Task<bool> DecidedAsync(Guid tenantId, Guid subscriptionId, string module, bool approved,
        string requesterEmail, decimal? acceptedPrice, string? currency, string? reason, CancellationToken ct)
    {
        try
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            var normalized = requesterEmail.Trim().ToUpperInvariant();
            var requester = await db.Users.IgnoreQueryFilters().AsNoTracking()
                // NormalizedEmail is Identity's; a user written outside UserManager may lack it.
                .Where(u => u.TenantId == tenantId
                            && (u.NormalizedEmail == normalized || u.Email!.ToUpper() == normalized))
                .Select(u => new { u.Id, u.FullName, u.PreferredLanguage })
                .FirstOrDefaultAsync(ct);

            var label = Label(module);
            var data = new Dictionary<string, object>
            {
                ["full_name"] = requester?.FullName ?? string.Empty,
                ["company_name"] = tenant?.Name ?? string.Empty,
                ["module_label"] = label,
                ["accepted_price"] = acceptedPrice is { } p ? Price(p, currency ?? string.Empty) : string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["modules_url"] = ModulesUrl(tenant?.Fqdn),
            };

            await notifications.QueueEmailAsync(new QueueEmailRequest(
                TemplateKey: approved ? ApprovedTemplate : RejectedTemplate,
                RecipientEmail: requesterEmail,
                RecipientName: requester?.FullName,
                Module: "Administration",
                Locale: requester?.PreferredLanguage ?? tenant?.DefaultLanguage ?? "fr",
                TemplateData: data,
                IdempotencyKey: $"tenant-module.decided:{subscriptionId}",
                TenantId: tenantId), ct);

            if (requester is not null)
            {
                await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                    TenantId: tenantId,
                    UserId: requester.Id,
                    Category: "tenant-module.decision",
                    Title: approved ? $"Module « {label} » activé" : $"Demande du module « {label} » refusée",
                    Body: approved
                        ? "L'équipe Sankoré a autorisé votre demande."
                        : "L'équipe Sankoré a refusé votre demande ; le motif est sur la page Modules.",
                    Link: ModulesPath,
                    IdempotencyKey: $"tenant-module.decided:{subscriptionId}"), ct);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not notify the decision on module subscription {SubscriptionId} (tenant {TenantId})",
                subscriptionId, tenantId);
            return false;
        }
    }

    private async Task<int> ToPlatformAsync(Guid tenantId, string template, string key,
        Dictionary<string, object> data, CancellationToken ct)
    {
        var recipients = options.Value.PlatformRecipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            logger.LogWarning(
                "No {Section}:PlatformRecipients configured: '{Template}' for tenant {TenantId} not mailed",
                TenantModuleNotificationOptions.SectionName, template, tenantId);
            return 0;
        }

        try
        {
            var tenant = await tenantStore.GetAsync(tenantId, ct);
            data["company_name"] = tenant?.Name ?? string.Empty;
            data["full_name"] = string.Empty;

            var sent = 0;
            foreach (var recipient in recipients)
            {
                await notifications.QueueEmailAsync(new QueueEmailRequest(
                    TemplateKey: template,
                    RecipientEmail: recipient,
                    RecipientName: null,
                    Module: "Administration",
                    // The platform team reads French; the tenant's own language does not apply.
                    Locale: "fr",
                    TemplateData: new Dictionary<string, object>(data),
                    IdempotencyKey: $"{key}:{recipient.ToLowerInvariant()}",
                    TenantId: tenantId), ct);
                sent++;
            }

            return sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue '{Template}' for tenant {TenantId}; the act itself stands",
                template, tenantId);
            return 0;
        }
    }

    private static string ModulesUrl(string? fqdn) =>
        string.IsNullOrWhiteSpace(fqdn) ? ModulesPath : $"{fqdn.TrimEnd('/')}{ModulesPath}";
}
