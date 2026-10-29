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

    /// <summary>Who works the record and where. Null when the record does not exist.</summary>
    private async Task<RecordFacts?> FactsAsync(WorkflowNotificationRequested request, CancellationToken ct)
    {
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

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..(max - 1)] + "…";

    private sealed record RecordFacts(
        Guid? AgencyId, Guid? AgentId, string? FirstName, string? LastName, string? Product, string? Link);
}
