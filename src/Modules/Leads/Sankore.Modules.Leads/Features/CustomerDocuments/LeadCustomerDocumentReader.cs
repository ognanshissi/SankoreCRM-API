namespace Sankore.Modules.Leads.Features.CustomerDocuments;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The files this module holds about a client, for the client record's « Documents » tab:
/// attachments of their projects and of the tasks about them, and the media of their WhatsApp
/// conversations. Metadata only; each file opens through its own endpoint.
/// </summary>
/// <remarks>
/// <b>Whose is it.</b> A lead is the client's when it is attached to them or was converted into
/// them. The client's projects are those on the client or on one of those leads; their tasks are
/// those about the client or about one of those leads; their conversations are those of those
/// leads and those matched to the client directly. A deleted attachment and a purged media are left out — there is nothing left to open.
/// </remarks>
internal static class LeadCustomerDocumentReader
{
    private static readonly WhatsAppMessageKind[] MediaKinds =
    [
        WhatsAppMessageKind.Image,
        WhatsAppMessageKind.Document,
        WhatsAppMessageKind.Audio,
        WhatsAppMessageKind.Video,
        WhatsAppMessageKind.Sticker,
    ];

    public static async Task<IReadOnlyList<CustomerDocumentEntry>> ReadAsync(
        LeadsDbContext db, Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var leadIds = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && (l.CustomerId == customerId || l.ConvertedToCustomerId == customerId))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var projects = await db.Opportunities
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                     && ((o.CustomerEntityType == "Customer" && o.CustomerEntityId == customerId)
                         || (o.LeadId != null && leadIds.Contains(o.LeadId.Value))))
            .Select(o => new { o.Id, o.Title })
            .ToListAsync(ct);
        var projectTitles = projects.ToDictionary(p => p.Id, p => p.Title);
        var projectIds = projectTitles.Keys.ToList();

        var entries = new List<CustomerDocumentEntry>();

        var projectFiles = await db.ProjectAttachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.DeletedAt == null && projectIds.Contains(a.OpportunityId))
            .ToListAsync(ct);

        entries.AddRange(projectFiles.Select(a => new CustomerDocumentEntry(
            Key: $"project-attachment:{a.Id}",
            Source: "Project",
            Direction: CustomerDocumentDirection.Collected,
            Kind: "ProjectAttachment",
            FileName: a.FileName,
            ContentType: a.ContentType,
            SizeBytes: a.SizeBytes,
            At: a.UploadedAt,
            Status: null,
            Detail: a.FieldKey,
            OriginType: "Project",
            OriginId: a.OpportunityId,
            OriginLabel: projectTitles.GetValueOrDefault(a.OpportunityId),
            ContentPath: $"leads/opportunities/{a.OpportunityId}/attachments/{a.Id}/content",
            OpenPermissions: [Permissions.CanReadOpportunities.Code])));

        var tasks = await db.CrmTasks
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId
                     && ((t.EntityType == TaskEntityType.Client && t.EntityId == customerId)
                         || (t.EntityType == TaskEntityType.Lead && t.EntityId != null && leadIds.Contains(t.EntityId.Value))))
            .Select(t => new { t.Id, t.Title })
            .ToListAsync(ct);
        var taskTitles = tasks.ToDictionary(t => t.Id, t => t.Title);
        var taskIds = taskTitles.Keys.ToList();

        var taskFiles = await db.CrmTaskAttachments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.DeletedAt == null && taskIds.Contains(a.TaskId))
            .ToListAsync(ct);

        entries.AddRange(taskFiles.Select(a => new CustomerDocumentEntry(
            Key: $"task-attachment:{a.Id}",
            Source: "Task",
            Direction: CustomerDocumentDirection.Collected,
            Kind: "TaskAttachment",
            FileName: a.FileName,
            ContentType: a.ContentType,
            SizeBytes: a.SizeBytes,
            At: a.UploadedAt,
            Status: null,
            Detail: null,
            OriginType: "Task",
            OriginId: a.TaskId,
            OriginLabel: taskTitles.GetValueOrDefault(a.TaskId),
            ContentPath: $"leads/tasks/{a.TaskId}/attachments/{a.Id}/content",
            OpenPermissions: [Permissions.CanReadCrmTasks.Code])));

        // The client's conversations: their leads', and those matched to the client directly.
        // A media is a message of a media kind not yet purged — the provider's id is null both
        // before an outbound upload completes and after a purge, so it cannot be the test.
        var media = await db.WhatsAppMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.MediaPurgedAt == null && MediaKinds.Contains(m.Kind))
            .Join(db.WhatsAppConversations.IgnoreQueryFilters()
                    .Where(c => c.TenantId == tenantId
                             && (c.ClientId == customerId || (c.LeadId != null && leadIds.Contains(c.LeadId.Value)))),
                m => m.ConversationId, c => c.Id, (m, c) => new { Message = m, Conversation = c })
            .ToListAsync(ct);

        entries.AddRange(media.Select(x => new CustomerDocumentEntry(
            Key: $"whatsapp-media:{x.Message.Id}",
            Source: "WhatsApp",
            Direction: x.Message.Direction == WhatsAppDirection.Inbound
                ? CustomerDocumentDirection.Collected
                : CustomerDocumentDirection.Delivered,
            Kind: "WhatsAppMedia",
            FileName: x.Message.MediaFileName,
            ContentType: x.Message.MediaMimeType,
            SizeBytes: x.Message.MediaSizeBytes,
            At: x.Message.SentAt ?? x.Message.CreatedAt,
            Status: null,
            Detail: x.Message.Kind.ToString(),
            OriginType: x.Conversation.LeadId is null ? "Conversation" : "Lead",
            OriginId: x.Conversation.LeadId ?? x.Conversation.Id,
            OriginLabel: "Conversation WhatsApp",
            // The conversation route serves a conversation with or without a lead; it accepts
            // any of the three WhatsApp roles.
            ContentPath: $"leads/whatsapp/conversations/{x.Conversation.Id}/messages/{x.Message.Id}/media",
            OpenPermissions:
            [
                Permissions.CanSendWhatsApp.Code,
                Permissions.CanReplyWhatsApp.Code,
                Permissions.CanSuperviseWhatsApp.Code,
            ])));

        return [.. entries.OrderByDescending(e => e.At)];
    }
}
