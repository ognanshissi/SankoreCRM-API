using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Notifications;

/// <summary>
/// Tells the PEOPLE WORKING a record — its agent, the holders of a role — that a « Notification »
/// step was reached, in their bell. The person OF the record is written to by the module that owns
/// them (Leads, Customers); this side needs no address, only who works the record and where.
/// </summary>
public sealed class WorkflowNotificationInternalConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowNotificationInternalConsumer> logger) : IConsumer<WorkflowNotificationRequested>
{
    public async Task Consume(ConsumeContext<WorkflowNotificationRequested> context)
    {
        var request = context.Message;
        if (!request.Tells(WorkflowNotificationRecipients.AssignedAgent)
            && !request.Tells(WorkflowNotificationRecipients.Role))
            return;

        using var bgCtx = BackgroundJobContext.SetScope(request.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var told = await scope.ServiceProvider.GetRequiredService<InternalWorkflowNotifier>()
            .SendAsync(request, context.CancellationToken);

        logger.LogInformation(
            "Workflow notification {Key} on {EntityType} {EntityId}: {Count} user(s) told in-app.",
            request.IdempotencyKey, request.EntityType, request.EntityId, told);

        await context.Publish(told > 0
            ? WorkflowNotificationOutcome.Queued(request, told == 1
                ? "1 utilisateur prévenu dans l'application."
                : $"{told} utilisateurs prévenus dans l'application.")
            : WorkflowNotificationOutcome.NotSent(request, "aucun agent ni titulaire du rôle à prévenir."),
            context.CancellationToken);
    }
}

public sealed class InternalWorkflowNotifier(
    ILeadsModule leads,
    ICustomersModule customers,
    IKycModule kyc,
    IAdministrationModule administration,
    INotificationsModule notifications,
    ITenantStore tenantStore)
{
    public const string Category = "workflow.notification";

    private const int MaxTitle = 150;
    private const int MaxBody = 400;

    /// <summary>The number of users notified.</summary>
    public async Task<int> SendAsync(WorkflowNotificationRequested request, CancellationToken ct)
    {
        var facts = await FactsAsync(request, ct);
        if (facts is null) return 0;

        var users = new HashSet<Guid>();
        if (request.Tells(WorkflowNotificationRecipients.AssignedAgent) && facts.AgentId is { } agent)
            users.Add(agent);
        if (request.Tells(WorkflowNotificationRecipients.Role) && request.RoleCodes.Count > 0)
            users.UnionWith(await administration.ListActiveUsersInRolesAsync(
                request.TenantId, request.RoleCodes, facts.AgencyId, ct));
        if (users.Count == 0) return 0;

        var agentName = facts.AgentId is { } a
            ? (await administration.GetUserNamesAsync(request.TenantId, [a], ct)).GetValueOrDefault(a)
            : null;
        var values = new WorkflowNotificationValues(
            facts.FirstName, facts.LastName, agentName, facts.Product,
            (await tenantStore.GetAsync(request.TenantId, ct))?.Name);

        var title = WorkflowNotificationText.Render(request.Subject, values);
        if (title.Length == 0) title = request.StepName;
        var body = WorkflowNotificationText.Render(request.Message, values);

        var told = 0;
        foreach (var user in users)
        {
            var result = await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                request.TenantId, user, Category,
                Clip(title, MaxTitle)!,
                Clip(body, MaxBody),
                facts.Link,
                $"{request.IdempotencyKey}:user:{user}"), ct);
            if (result.IsSuccess) told++;
        }
        return told;
    }

    private Task<RecordFacts?> FactsAsync(WorkflowNotificationRequested request, CancellationToken ct)
        => new RecordFactsReader(leads, customers, kyc).ReadAsync(request.TenantId, request.EntityType, request.EntityId, ct);

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>Who works a record and where — its agency, its agent, the names a message may quote, its screen.</summary>
public sealed record RecordFacts(
    Guid? AgencyId, Guid? AgentId, string? FirstName, string? LastName, string? Product, string? Link);

/// <summary>Reads <see cref="RecordFacts"/> from the module that owns the record.</summary>
public sealed class RecordFactsReader(ILeadsModule leads, ICustomersModule customers, IKycModule kyc)
{
    /// <summary>Null when the record does not exist.</summary>
    public async Task<RecordFacts?> ReadAsync(Guid tenantId, string entityType, Guid entityId, CancellationToken ct)
    {
        var request = new { TenantId = tenantId, EntityType = entityType, EntityId = entityId };
        switch (request.EntityType.ToLowerInvariant())
        {
            case "lead":
            case "opportunity":
            {
                var lead = await leads.GetNotificationFactsAsync(request.TenantId, request.EntityType, request.EntityId, ct);
                return lead is null ? null
                    : new RecordFacts(lead.AgencyId, lead.AssignedUserId, lead.FirstName, lead.LastName, lead.Product, lead.Link);
            }
            case "account":
            {
                var client = await customers.GetClientSummaryAsync(request.TenantId, request.EntityId, ct);
                return client is null ? null
                    : new RecordFacts(client.AgencyId, client.AdvisorUserId, null, client.DisplayName, null, $"/customers/{client.Id}");
            }
            case "kycfile":
            {
                var file = await kyc.GetKycFileSummaryAsync(request.TenantId, request.EntityId, ct);
                if (file is null) return null;
                var client = await customers.GetClientSummaryAsync(request.TenantId, file.CustomerId, ct);
                return new RecordFacts(file.AgencyId ?? client?.AgencyId, null, null, client?.DisplayName, null, $"/kyc/{file.Id}");
            }
            default:
                // No owner module to ask (a contact): the role is told tenant-wide, nobody else.
                return new RecordFacts(null, null, null, null, null, null);
        }
    }

    /// <summary>
    /// <see cref="ReadAsync"/> for many records, a few reads in all instead of one or two per record.
    /// A record that does not exist is absent; an entity type nobody owns reads as empty facts.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string EntityType, Guid EntityId), RecordFacts>> ReadManyAsync(
        Guid tenantId, IReadOnlyCollection<(string EntityType, Guid EntityId)> records, CancellationToken ct)
    {
        var result = new Dictionary<(string, Guid), RecordFacts>();
        var byKind = records.Distinct().ToLookup(r => r.EntityType.ToLowerInvariant());

        var leadIds = byKind["lead"].Select(r => r.EntityId).ToList();
        var projectIds = byKind["opportunity"].Select(r => r.EntityId).ToList();
        if (leadIds.Count + projectIds.Count > 0)
        {
            var found = await leads.GetNotificationFactsManyAsync(tenantId, leadIds, projectIds, ct);
            foreach (var r in byKind["lead"].Concat(byKind["opportunity"]))
                if (found.TryGetValue(r.EntityId, out var f))
                    result[r] = new RecordFacts(f.AgencyId, f.AssignedUserId, f.FirstName, f.LastName, f.Product, f.Link);
        }

        var files = byKind["kycfile"].Any()
            ? await kyc.GetKycFileSummariesAsync(tenantId, byKind["kycfile"].Select(r => r.EntityId).ToList(), ct)
            : new Dictionary<Guid, KycFileSummary>();
        var clientIds = byKind["account"].Select(r => r.EntityId).Concat(files.Values.Select(f => f.CustomerId)).Distinct().ToList();
        var clients = clientIds.Count == 0
            ? new Dictionary<Guid, ClientSummary>()
            : await customers.GetClientSummariesAsync(tenantId, clientIds, ct);

        foreach (var r in byKind["account"])
            if (clients.TryGetValue(r.EntityId, out var client))
                result[r] = new RecordFacts(client.AgencyId, client.AdvisorUserId, null, client.DisplayName, null, $"/customers/{client.Id}");
        foreach (var r in byKind["kycfile"])
            if (files.TryGetValue(r.EntityId, out var file))
            {
                var client = clients.GetValueOrDefault(file.CustomerId);
                result[r] = new RecordFacts(file.AgencyId ?? client?.AgencyId, null, null, client?.DisplayName, null, $"/kyc/{file.Id}");
            }

        foreach (var r in records.Where(r => r.EntityType.ToLowerInvariant() is not ("lead" or "opportunity" or "account" or "kycfile")))
            result[r] = new RecordFacts(null, null, null, null, null, null);
        return result;
    }

}
