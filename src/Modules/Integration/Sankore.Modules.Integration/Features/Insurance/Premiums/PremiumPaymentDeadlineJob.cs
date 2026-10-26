namespace Sankore.Modules.Integration.Features.Insurance.Premiums;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// The 60-day derogation's deadline (insurance lot 2): a premium still owed on a contract sent
/// without it is chased with the agent who subscribed it, seven days before the deadline and on the
/// day. The insurer, not this platform, decides what an unpaid deadline does to the cover.
/// </summary>
public sealed class PremiumPaymentDeadlineJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PremiumPaymentDeadlineJob> logger)
{
    public const string TemplateKey = "insurance.premium-payment-due";

    private static readonly int[] Thresholds = [7, 0];

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var administration = sp.GetRequiredService<IAdministrationModule>();
        var customers = sp.GetRequiredService<ICustomersModule>();
        var today = DateOnly.FromDateTime(sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);

        var owed = (await db.InsuranceSubscriptions
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.PaymentDueBy != null && s.PremiumPaidAt == null)
                .ToListAsync(ct))
            .Where(s => s.IsPremiumOutstanding)
            .Select(s => (Saga: s, DaysLeft: s.PaymentDueBy!.Value.DayNumber - today.DayNumber))
            .Where(x => Thresholds.Contains(x.DaysLeft))
            .ToList();

        foreach (var (saga, daysLeft) in owed)
        {
            try
            {
                var client = await customers.GetClientSummaryAsync(tenantId, saga.CrmCustomerId, ct);

                await administration.NotifyUserAsync(new NotifyUserRequest(
                    TenantId: tenantId,
                    UserId: saga.CreatedBy,
                    TemplateKey: TemplateKey,
                    IdempotencyKey: $"{TemplateKey}:{saga.Id}:{daysLeft}",
                    TemplateData: new Dictionary<string, object>
                    {
                        ["client_name"] = client?.DisplayName ?? "—",
                        ["policy_reference"] = saga.InsurerPolicyReference ?? string.Empty,
                        ["amount"] = string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{saga.PremiumAmount:N0} {saga.Currency}"),
                        ["due_on"] = saga.PaymentDueBy!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                        ["days_left"] = daysLeft,
                    }), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed reminder must not cost the others; tomorrow's run does not resend it,
                // so the miss is logged where an operator looks.
                logger.LogError(ex, "Premium reminder for subscription {SubscriptionId} could not be queued", saga.Id);
            }
        }
    }
}
