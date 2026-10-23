using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Support.PlatformSide;
using Sankore.Admin.Features.Support.TenantSide;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Support;

/// <summary>
/// Support tickets a tenant user addresses to the platform. Two groups, by audience:
/// <list type="bullet">
/// <item><c>/api/v1/tenants/{tenantId}/support</c> — what Sankore.Api calls with the service key
/// on a tenant user's behalf: open, list, read, reply, close, reopen, and the activity feed it
/// turns into e-mails;</item>
/// <item><c>/api/v1/support</c> — the platform's queue and answers, all <c>RequirePlatformAdmin</c>.</item>
/// </list>
/// </summary>
public static class SupportEndpoints
{
    public static void MapSupportEndpoints(this WebApplication app)
    {
        var tenantTickets = app.MapGroup("/api/v1/tenants/{tenantId:guid}/support/tickets")
            .WithTags("Tenant support");

        ListTenantSupportTicketsEndpoint.Map(tenantTickets);
        OpenSupportTicketEndpoint.Map(tenantTickets);
        GetTenantSupportTicketEndpoint.Map(tenantTickets);
        TenantSupportActionEndpoints.Map(tenantTickets);

        ListTenantSupportActivityEndpoint.Map(app.MapGroup("/api/v1/tenants/{tenantId:guid}/support/activity")
            .WithTags("Tenant support"));

        var platformTickets = app.MapGroup("/api/v1/support/tickets")
            .WithTags("Support");

        ListPlatformSupportTicketsEndpoint.Map(platformTickets);
        GetPlatformSupportTicketEndpoint.Map(platformTickets);
        PlatformSupportActionEndpoints.Map(platformTickets);

        ListSupportAssigneesEndpoint.Map(app.MapGroup("/api/v1/support/assignees").WithTags("Support"));
    }
}

/// <summary>
/// Closes the tickets left Resolved for <see cref="SupportTicket.AutoCloseDelay"/> with no answer.
/// Hourly; the registry has no Hangfire. Running it on two replicas at once is harmless — the
/// second one finds nothing left Resolved.
/// </summary>
public sealed class SupportAutoCloseService(IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<SupportAutoCloseService> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    internal const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, clock);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
                var closed = await CloseStaleAsync(db, clock.GetUtcNow(), stoppingToken);
                if (closed > 0) logger.LogInformation("{Count} resolved support ticket(s) closed automatically", closed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Automatic closing of resolved support tickets failed; next try in {Period}", Period);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static async Task<int> CloseStaleAsync(AdminDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - SupportTicket.AutoCloseDelay;
        var total = 0;
        while (true)
        {
            var stale = await db.SupportTickets
                .Include(t => t.Messages)
                .Where(t => t.Status == SupportTicketStatus.Resolved && t.ResolvedAt <= threshold)
                .OrderBy(t => t.ResolvedAt)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (stale.Count == 0) return total;

            total += stale.Count(t => t.AutoClose(now));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            if (stale.Count < BatchSize) return total;
        }
    }
}
