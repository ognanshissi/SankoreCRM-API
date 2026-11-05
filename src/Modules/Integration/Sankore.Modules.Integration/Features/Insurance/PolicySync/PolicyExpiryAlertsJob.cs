namespace Sankore.Modules.Integration.Features.Insurance.PolicySync;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// The bell for an insurance contract about to end (2026-11-05, product owner): the client's advisor,
/// else the head of the client's agency, once for each end date. Insurance only for now: it is the one
/// kind of contract whose end date reaches us, no core banking connector answering for its loans yet.
/// </summary>
public sealed class PolicyExpiryAlertsJob(IServiceScopeFactory scopeFactory, ILogger<PolicyExpiryAlertsJob> logger)
{
    public const string Category = "insurance.policy-expiring";
    public const int WindowDays = 30;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var today = DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var sent = await SendAsync(
            tenantId,
            today,
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<ICustomersModule>(),
            sp.GetRequiredService<IAdministrationModule>(),
            sp.GetRequiredService<INotificationsModule>(),
            ct);

        if (sent > 0) logger.LogInformation("{Count} policy expiry alert(s) for tenant {TenantId}.", sent, tenantId);
    }

    internal static async Task<int> SendAsync(
        Guid tenantId,
        DateOnly today,
        IntegrationDbContext db,
        ICustomersModule customers,
        IAdministrationModule administration,
        INotificationsModule notifications,
        CancellationToken ct)
    {
        var until = today.AddDays(WindowDays);
        var ending = await db.Policies
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Status == PolicyStatus.Issued
                     && p.ExpiryDate != null && p.ExpiryDate >= today && p.ExpiryDate <= until)
            .Select(p => new { p.Id, p.CrmCustomerId, p.PolicyNumber, Expiry = p.ExpiryDate!.Value })
            .ToListAsync(ct);
        if (ending.Count == 0) return 0;

        var clients = await customers.GetClientSummariesAsync(
            tenantId, ending.Select(p => p.CrmCustomerId).Distinct().ToList(), ct);

        var count = 0;
        foreach (var policy in ending)
        {
            if (!clients.TryGetValue(policy.CrmCustomerId, out var client)) continue;

            var recipient = client.AdvisorUserId
                ?? await administration.ResolveAgencyRecipientAsync(tenantId, client.AgencyId, null, ct);
            if (recipient is not { } person) continue;

            var result = await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                tenantId, person, Category,
                "Contrat bientôt à échéance",
                $"Le contrat {policy.PolicyNumber} de {client.DisplayName} prend fin le {policy.Expiry:dd/MM/yyyy}.",
                $"/customers/{policy.CrmCustomerId}/contrats",
                $"policy-expiring-{policy.Id}-{policy.Expiry:yyyyMMdd}-{person}"), ct);
            if (result.IsSuccess) count++;
        }
        return count;
    }
}
