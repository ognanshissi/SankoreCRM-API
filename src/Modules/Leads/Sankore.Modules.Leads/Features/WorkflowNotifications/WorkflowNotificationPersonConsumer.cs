using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.WorkflowNotifications;

/// <summary>
/// Writes to the PERSON of a lead or a project when a workflow « Notification » step asks for it.
/// Here because the address, its encryption and the consent are this module's.
///
/// <para>
/// <b>A lead is a prospect: the channel's consent is required</b> (« contact par e-mail » or « par
/// SMS », active, given by the person themself). The notification module is transactional-only, and
/// a workflow message to somebody who is not yet a client is a commercial one. A project opened for
/// a client goes through M01, which owns that address and refuses a client whose processing is
/// restricted.
/// </para>
///
/// <para>
/// Never throws for « cannot send »: no address, no consent, no SMS channel — the step is already
/// franchie and the circuit goes on. The reason is logged. Idempotent through the queue keys:
/// a redelivered request is dropped as <c>DUPLICATE_IDEMPOTENCY_KEY</c>.
/// </para>
/// </summary>
public sealed class WorkflowNotificationPersonConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowNotificationPersonConsumer> logger) : IConsumer<WorkflowNotificationRequested>
{
    public async Task Consume(ConsumeContext<WorkflowNotificationRequested> context)
    {
        var request = context.Message;
        if (!request.Tells(WorkflowNotificationRecipients.Subject)) return;

        var isLead = string.Equals(request.EntityType, "Lead", StringComparison.OrdinalIgnoreCase);
        var isProject = string.Equals(request.EntityType, "Opportunity", StringComparison.OrdinalIgnoreCase);
        if (!isLead && !isProject) return;

        using var bgCtx = BackgroundJobContext.SetScope(request.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<LeadWorkflowNotifier>();

        var outcome = await sender.SendAsync(request, context.CancellationToken);
        logger.LogInformation(
            "Workflow notification {Key} to the person of {EntityType} {EntityId}: {Outcome}.",
            request.IdempotencyKey, request.EntityType, request.EntityId, outcome.Detail);

        // Recorded on the instance by Workflow, so the step says what happened.
        await context.Publish(outcome, context.CancellationToken);
    }
}

/// <summary>The sending itself, apart from the consumer so a test can drive it without a bus.</summary>
public sealed class LeadWorkflowNotifier(
    LeadsDbContext db,
    INotificationsModule notifications,
    IAdministrationModule administration,
    ICustomersModule customers,
    ITenantStore tenantStore)
{
    /// <summary>Queued, or why nothing was — one French sentence for the instance screen.</summary>
    public async Task<WorkflowNotificationOutcome> SendAsync(WorkflowNotificationRequested request, CancellationToken ct)
    {
        Guid? leadId = request.EntityId;
        string? product = null;
        Guid? projectOwner = null;
        Guid? clientId = null;

        if (string.Equals(request.EntityType, "Opportunity", StringComparison.OrdinalIgnoreCase))
        {
            var project = await db.Opportunities
                .IgnoreQueryFilters()
                .Where(o => o.TenantId == request.TenantId && o.Id == request.EntityId)
                .Select(o => new { o.LeadId, o.CustomerEntityType, o.CustomerEntityId, o.Product, o.OwnerId })
                .FirstOrDefaultAsync(ct);
            if (project is null) return WorkflowNotificationOutcome.NotSent(request, "le projet n'existe plus.");

            leadId = project.LeadId;
            product = project.Product;
            projectOwner = project.OwnerId;
            if (project.CustomerEntityType == "Customer" && project.CustomerEntityId is { } customer)
                clientId = customer;
        }

        var lead = leadId is { } id
            ? await db.Leads
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(l => l.TenantId == request.TenantId && l.Id == id, ct)
            : null;

        var institution = (await tenantStore.GetAsync(request.TenantId, ct))?.Name;
        var agentId = projectOwner ?? lead?.CurrentAssignedId ?? lead?.OwnerId;
        var agentName = agentId is { } a
            ? (await administration.GetUserNamesAsync(request.TenantId, [a], ct)).GetValueOrDefault(a)
            : null;

        if (clientId is { } client)
            return await SendToClientAsync(request, client, lead, product, agentName, institution, ct);

        if (lead is null) return WorkflowNotificationOutcome.NotSent(request, "aucune personne sur ce dossier.");
        if (lead.IsAnonymized) return WorkflowNotificationOutcome.NotSent(request, "les données du lead ont été effacées.");

        var values = new WorkflowNotificationValues(
            lead.FirstName,
            lead.LastName ?? (lead.FirstName is null ? lead.FullName : null),
            agentName,
            product ?? LeadNotificationValues.Product(lead.InterestedProduct),
            institution);
        var body = WorkflowNotificationText.Render(request.Message, values);
        if (body.Length == 0) return WorkflowNotificationOutcome.NotSent(request, "le message est vide.");

        var locale = LanguageCode.Normalize(lead.PreferredLanguage);
        var data = new Dictionary<string, object>
        {
            ["subject"] = Subject(request, values),
            ["body"] = body,
            ["company_name"] = institution ?? string.Empty,
        };

        if (request.IsSms)
        {
            if (!await HasConsentAsync(lead, ConsentType.SmsContact, ct)) return WorkflowNotificationOutcome.NotSent(request, "le lead n'a pas consenti au contact par SMS.");

            var sms = await administration.GetSmsConfigAsync(request.TenantId, ct);
            if (sms is null || !sms.CanSend) return WorkflowNotificationOutcome.NotSent(request, "aucun canal SMS n'est configuré.");

            var phone = PhoneAddress.ToE164(lead.PhoneNumber, sms.DefaultCallingCode);
            if (phone is null) return WorkflowNotificationOutcome.NotSent(request, "le numéro du lead est inutilisable.");

            var queued = await notifications.QueueSmsAsync(new QueueSmsRequest(
                WorkflowNotificationRequested.TemplateKey, phone, "Leads", locale, data,
                $"{request.IdempotencyKey}:subject:sms", request.TenantId), ct);
            return Outcome(request, queued, "SMS mis en file pour le lead.");
        }

        if (string.IsNullOrWhiteSpace(lead.Email)) return WorkflowNotificationOutcome.NotSent(request, "le lead n'a pas d'adresse e-mail.");
        if (!await HasConsentAsync(lead, ConsentType.EmailContact, ct)) return WorkflowNotificationOutcome.NotSent(request, "le lead n'a pas consenti au contact par e-mail.");

        var mailed = await notifications.QueueEmailAsync(new QueueEmailRequest(
            WorkflowNotificationRequested.TemplateKey, lead.Email, lead.FullName, "Leads", locale, data,
            $"{request.IdempotencyKey}:subject:email", request.TenantId), ct);
        return Outcome(request, mailed, "E-mail mis en file pour le lead.");
    }

    /// <summary>
    /// A project opened for a client: M01 holds the address and refuses a restricted client. The
    /// names come from the lead the project grew from, else from the client's display name.
    /// </summary>
    private async Task<WorkflowNotificationOutcome> SendToClientAsync(
        WorkflowNotificationRequested request, Guid clientId, Lead? lead, string? product,
        string? agentName, string? institution, CancellationToken ct)
    {
        if (await customers.IsProcessingRestrictedAsync(request.TenantId, clientId, ct))
            return WorkflowNotificationOutcome.NotSent(request, "le traitement des données du client est restreint.");

        var summary = lead is null ? await customers.GetClientSummaryAsync(request.TenantId, clientId, ct) : null;
        var values = new WorkflowNotificationValues(
            lead?.FirstName,
            lead?.LastName ?? summary?.DisplayName,
            agentName,
            product,
            institution);
        var body = WorkflowNotificationText.Render(request.Message, values);
        if (body.Length == 0) return WorkflowNotificationOutcome.NotSent(request, "le message est vide.");

        var data = new Dictionary<string, object> { ["subject"] = Subject(request, values), ["body"] = body };

        var sent = request.IsSms
            ? await customers.NotifyClientBySmsAsync(new NotifyClientBySmsRequest(
                request.TenantId, clientId, WorkflowNotificationRequested.TemplateKey,
                $"{request.IdempotencyKey}:subject:sms", data), ct)
            : await customers.NotifyClientAsync(new NotifyClientRequest(
                request.TenantId, clientId, WorkflowNotificationRequested.TemplateKey,
                $"{request.IdempotencyKey}:subject:email", data), ct);

        return sent
            ? WorkflowNotificationOutcome.Queued(request, request.IsSms ? "SMS mis en file pour le client." : "E-mail mis en file pour le client.")
            : WorkflowNotificationOutcome.NotSent(request, request.IsSms
                ? "pas de numéro utilisable pour le client, ou pas de canal SMS."
                : "pas d'adresse e-mail utilisable pour le client.");
    }

    private static string Subject(WorkflowNotificationRequested request, WorkflowNotificationValues values)
    {
        var subject = WorkflowNotificationText.Render(request.Subject, values);
        return subject.Length > 0 ? subject : request.StepName;
    }

    /// <summary>The person's own consent: not a household member's (those carry a subject id).</summary>
    private Task<bool> HasConsentAsync(Lead lead, ConsentType type, CancellationToken ct)
        => db.LeadConsents
            .IgnoreQueryFilters()
            .AnyAsync(c => c.TenantId == lead.TenantId
                        && c.LeadId == lead.Id
                        && c.Type == type
                        && c.SubjectId == null
                        && c.Status == ConsentStatus.Active, ct);

    private static WorkflowNotificationOutcome Outcome(WorkflowNotificationRequested request, Result<Guid> queued, string detail)
        => queued.IsSuccess || queued.Error == "DUPLICATE_IDEMPOTENCY_KEY"
            ? WorkflowNotificationOutcome.Queued(request, detail)
            : WorkflowNotificationOutcome.NotSent(request, $"refusé par le module de notification ({queued.Error}).");
}

internal static class LeadNotificationValues
{
    /// <summary>
    /// What the prospect asked for, readable in a message. « Unknown » is the default the pull job
    /// and the system capture write when a source names no product — not a product.
    /// </summary>
    public static string? Product(string? interestedProduct)
        => string.IsNullOrWhiteSpace(interestedProduct) || interestedProduct == "Unknown" ? null : interestedProduct;
}
