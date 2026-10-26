namespace Sankore.Modules.Customers.Features.Complaints;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The e-mail and the bell for a complaint that needs somebody (2026-10-26). The task is M13's,
/// from the same event. No inbox table: both sends drop a key they have already seen, and the key
/// is the complaint plus WHY — a recording and a late deadline are two distinct alerts.
/// </summary>
public sealed class ClientComplaintNotificationsConsumer(
    CustomersDbContext db,
    IAdministrationModule administration,
    INotificationsModule notifications,
    ITenantStore tenantStore)
    : IConsumer<ClientComplaintActionRequiredEvent>
{
    public const string TemplateKey = "client-complaint.action-required";

    public async Task Consume(ConsumeContext<ClientComplaintActionRequiredEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.TenantId == Guid.Empty || evt.RecipientUserId == Guid.Empty) return;

        var clientName = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == evt.TenantId && c.Id == evt.ClientId)
            .Select(c => c.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Client";

        var fqdn = (await tenantStore.GetAsync(evt.TenantId, ct))?.Fqdn;

        var key = $"{TemplateKey}:{evt.ComplaintId}:{evt.Reason}";
        var (title, situation) = Wording(evt.Reason, evt.Reference);

        // Never what the client wrote: a rendered body travels to a mail provider and sits in its
        // delivery log. The subject is an operator's line, and the record is one click away.
        await administration.NotifyUserAsync(new NotifyUserRequest(
            evt.TenantId, evt.RecipientUserId, TemplateKey, key,
            new Dictionary<string, object>
            {
                ["complaint_reference"] = evt.Reference,
                ["complaint_subject"] = evt.Subject,
                ["client_name"] = clientName,
                ["situation"] = situation,
                ["due_at"] = evt.DueAt.ToString("dd/MM/yyyy"),
                ["complaint_url"] = ComplaintsUrl(fqdn, evt.ClientId),
            }), ct);

        await notifications.NotifyInAppAsync(new InAppNotificationRequest(
            evt.TenantId, evt.RecipientUserId, "customers.complaint", title,
            $"{clientName} — {evt.Subject}", ComplaintsPath(evt.ClientId), key), ct);
    }

    /// <summary>The client record's complaints tab, which the front serves under this path.</summary>
    internal static string ComplaintsPath(Guid clientId) => $"/customers/{clientId}/reclamations";

    // Same shape as the support e-mails: the tenant's domain as the registry stores it, the bare
    // path when it has none (the message still names the reference to look up).
    internal static string ComplaintsUrl(string? fqdn, Guid clientId) =>
        string.IsNullOrWhiteSpace(fqdn) ? ComplaintsPath(clientId) : $"{fqdn.TrimEnd('/')}{ComplaintsPath(clientId)}";

    internal static (string Title, string Situation) Wording(string reason, string reference) => reason switch
    {
        ClientComplaintActionReasons.AcknowledgementOverdue =>
            ($"Réclamation {reference} : accusé de réception en retard",
             "L'accusé de réception de cette réclamation n'a pas été enregistré dans le délai."),
        ClientComplaintActionReasons.ResolutionOverdue =>
            ($"Réclamation {reference} : réponse en retard",
             "La réponse à cette réclamation n'a pas été enregistrée dans le délai."),
        _ =>
            ($"Nouvelle réclamation {reference}",
             "Une réclamation vous a été attribuée."),
    };
}
