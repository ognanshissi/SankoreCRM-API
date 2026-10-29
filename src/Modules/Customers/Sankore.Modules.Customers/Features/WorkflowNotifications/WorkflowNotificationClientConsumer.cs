using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Infrastructure.Notifications;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.WorkflowNotifications;

/// <summary>
/// Writes to the CLIENT of a workflow on a client record (<c>Account</c>) when a « Notification »
/// step asks for it, through the module's own notifiers — the only ones that decrypt its address.
/// A client is in a relationship with the institution, so no prospecting consent is asked; an
/// anonymised client or one whose processing is restricted is never written to. Never throws for
/// « cannot send »: the step is already franchie.
/// </summary>
public sealed class WorkflowNotificationClientConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowNotificationClientConsumer> logger) : IConsumer<WorkflowNotificationRequested>
{
    public async Task Consume(ConsumeContext<WorkflowNotificationRequested> context)
    {
        var request = context.Message;
        if (!request.Tells(WorkflowNotificationRecipients.Subject)) return;
        if (!string.Equals(request.EntityType, "Account", StringComparison.OrdinalIgnoreCase)) return;

        using var bgCtx = BackgroundJobContext.SetScope(request.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ClientWorkflowNotifier>()
            .SendAsync(request, context.CancellationToken);

        logger.LogInformation(
            "Workflow notification {Key} to client {ClientId}: {Outcome}.",
            request.IdempotencyKey, request.EntityId, outcome.Detail);

        // Recorded on the instance by Workflow, so the step says what happened.
        await context.Publish(outcome, context.CancellationToken);
    }
}

public sealed class ClientWorkflowNotifier(
    CustomersDbContext db,
    ClientEmailNotifier email,
    ClientSmsNotifier sms,
    IAdministrationModule administration,
    ITenantStore tenantStore)
{
    public async Task<WorkflowNotificationOutcome> SendAsync(WorkflowNotificationRequested request, CancellationToken ct)
    {
        var client = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == request.TenantId && c.Id == request.EntityId)
            .Select(c => new { c.FirstName, c.LastName, c.DisplayName, c.AdvisorUserId, c.IsAnonymized, c.ProcessingRestrictedAt })
            .FirstOrDefaultAsync(ct);

        if (client is null) return WorkflowNotificationOutcome.NotSent(request, "le client n'existe plus.");
        if (client.IsAnonymized) return WorkflowNotificationOutcome.NotSent(request, "le client a été anonymisé.");
        if (client.ProcessingRestrictedAt is not null) return WorkflowNotificationOutcome.NotSent(request, "le traitement des données du client est restreint.");

        var agentName = client.AdvisorUserId is { } advisor
            ? (await administration.GetUserNamesAsync(request.TenantId, [advisor], ct)).GetValueOrDefault(advisor)
            : null;

        var values = new WorkflowNotificationValues(
            client.FirstName,
            client.LastName ?? client.DisplayName,
            agentName,
            Product: null,
            Institution: (await tenantStore.GetAsync(request.TenantId, ct))?.Name);

        var body = WorkflowNotificationText.Render(request.Message, values);
        if (body.Length == 0) return WorkflowNotificationOutcome.NotSent(request, "le message est vide.");

        var subject = WorkflowNotificationText.Render(request.Subject, values);
        var data = new Dictionary<string, object>
        {
            ["subject"] = subject.Length > 0 ? subject : request.StepName,
            ["body"] = body,
        };

        var queued = request.IsSms
            ? await sms.NotifyAsync(new NotifyClientBySmsRequest(
                request.TenantId, request.EntityId, WorkflowNotificationRequested.TemplateKey,
                $"{request.IdempotencyKey}:subject:sms", data), ct)
            : await email.NotifyAsync(new NotifyClientRequest(
                request.TenantId, request.EntityId, WorkflowNotificationRequested.TemplateKey,
                $"{request.IdempotencyKey}:subject:email", data), ct);

        return queued
            ? WorkflowNotificationOutcome.Queued(request, request.IsSms ? "SMS mis en file pour le client." : "E-mail mis en file pour le client.")
            : WorkflowNotificationOutcome.NotSent(request, request.IsSms
                ? "pas de numéro utilisable pour le client, ou pas de canal SMS."
                : "pas d'adresse e-mail utilisable pour le client.");
    }
}
