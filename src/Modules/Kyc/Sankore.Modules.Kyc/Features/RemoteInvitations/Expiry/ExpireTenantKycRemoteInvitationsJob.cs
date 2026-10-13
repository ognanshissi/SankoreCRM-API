namespace Sankore.Modules.Kyc.Features.RemoteInvitations.Expiry;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — closes one tenant's lapsed collection links and tells the responsible agent
/// (KRM-B-04).
/// </summary>
public sealed class ExpireTenantKycRemoteInvitationsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>The seeded template telling an agent that a customer's link lapsed unused.</summary>
    public const string TemplateKey = "kyc.remote-link-expired";

    public async Task ExecuteAsync(Guid tenantId)
    {
        if (tenantId == Guid.Empty) return;

        // Set BEFORE the DI scope: ITenantContext is built from it and a job has no HTTP request.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var administration = scope.ServiceProvider.GetRequiredService<IAdministrationModule>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<ExpireTenantKycRemoteInvitationsJob>>();

        var now = clock.GetUtcNow();

        // A List and not the static array: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL.
        var activeStatuses = KycRemoteInvitation.ActiveStatuses.ToList();

        var lapsed = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(i => i.TenantId == tenantId
                     && i.ExpiresAt <= now
                     && activeStatuses.Contains(i.Status))
            .ToListAsync();

        if (lapsed.Count == 0) return;

        // The files, read in ONE query rather than per invitation: a tenant that imported a batch
        // of leads produces a batch of links that lapse together, and a lookup per row would turn
        // a nightly sweep into a few hundred round trips.
        var fileIds = lapsed.Select(i => i.KycFileId).Distinct().ToList();

        var files = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ResponsibleAgentId })
            .ToDictionaryAsync(f => f.Id, f => f.ResponsibleAgentId);

        var closed = 0;

        foreach (var invitation in lapsed)
        {
            if (invitation.Expire(clock))
                closed++;
        }

        // Committed BEFORE the notifications. The status change is the fact; the mails are a
        // courtesy, and a mail server being down must not leave a tenant's links sweeping again
        // tomorrow and mailing twice when it recovers.
        await db.SaveChangesAsync();

        foreach (var invitation in lapsed)
        {
            if (invitation.Status != KycRemoteInvitationStatus.Expired) continue;

            if (!files.TryGetValue(invitation.KycFileId, out var agentId) || agentId is not { } id)
            {
                // A file with no responsible agent — opened at a counter, or predating KRM-B-02.
                // Nothing to notify, and nothing wrong: the panel on the customer's file shows the
                // expired link to whoever opens it.
                continue;
            }

            // NotifyUserAsync never throws and resolves the agent's address, locale and name
            // itself — see IAdministrationModule. The payload carries no identity of the customer
            // beyond the file id the agent is being pointed at: a rendered body sits in a delivery
            // log at a mail provider.
            await administration.NotifyUserAsync(new NotifyUserRequest(
                TenantId: tenantId,
                UserId: id,
                TemplateKey: TemplateKey,
                // Keyed on the INVITATION, so a sweep that runs twice — a retry, a redeployment
                // mid-run — cannot mail the same agent about the same link again.
                IdempotencyKey: $"{TemplateKey}-{invitation.Id}",
                TemplateData: new Dictionary<string, object>
                {
                    ["kyc_file_id"] = invitation.KycFileId,
                    ["expired_at"] = invitation.ExpiresAt,
                }), CancellationToken.None);
        }

        logger.LogInformation(
            "Expired {Closed} remote-collection link(s) for tenant {TenantId}.", closed, tenantId);
    }
}
