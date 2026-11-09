namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Text.Json;
using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <param name="TemplateId">One of the tenant's synchronised templates.</param>
/// <param name="Variables">The values of <c>{{1}}</c>, <c>{{2}}</c>… in order — as the agent left them.</param>
public sealed record SendWhatsAppTemplateRequest(Guid TemplateId, IReadOnlyList<string>? Variables);

public sealed record SendWhatsAppTemplateResult(Guid ConversationId, Guid MessageId);

/// <param name="MayForce">
/// Whether the caller holds <c>WhatsApp.Send.Force</c>, read from the token by the endpoint —
/// never from the request body.
/// </param>
/// <param name="File">
/// The file of a template with a DOCUMENT or IMAGE header — set by a project's route only (project
/// documents lot 3). Sensitive: never in the audit row.
/// </param>
/// <param name="ProjectId">The project the file is from; the message is linked to it.</param>
internal sealed record SendWhatsAppTemplateCommand(
    Guid LeadId,
    Guid TemplateId,
    [property: SensitiveData] IReadOnlyList<string> Variables,
    bool MayForce,
    [property: SensitiveData] TemplateFile? File = null,
    Guid? ProjectId = null,
    Guid? ProjectAttachmentId = null)
    : IRequest<Result<SendWhatsAppTemplateResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

/// <summary>
/// An agent opens a WhatsApp exchange with a lead from its record (WA-BE-06).
///
/// <para>
/// <b>Every refusal of WA-BE-06 is checked here, in the order an agent can act on them</b>: the
/// lead must be theirs to work, consent must be given and not revoked, the number must be
/// writable in E.164, the institution's number must be active for the lead's agency, the
/// template approved and meant for the lead's product, one value per variable, and no template
/// may follow an unanswered one within 24 hours unless the caller may force it. The screen asks
/// the same questions through <see cref="WhatsAppEligibility"/> before offering the button.
/// </para>
///
/// <para>
/// <b>Nothing is sent from here.</b> The message is saved as « en file » and an event is written
/// to the outbox in the same transaction; the consumer hands it to the provider. A provider that
/// is down at this instant delays the message, and never loses it or the agent's click.
/// </para>
/// </summary>
internal sealed class SendWhatsAppTemplateHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    LeadAccessGuard accessGuard,
    IAdministrationModule admin,
    IIntegrationModule integration,
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer blindIndexer,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    Sankore.Modules.Customers.PublicApi.ICustomersModule? customers = null,
    IServiceProvider? services = null)
    : IRequestHandler<SendWhatsAppTemplateCommand, Result<SendWhatsAppTemplateResult>>
{
    /// <summary>Meta's own limit on one body parameter.</summary>
    internal const int MaxVariableLength = 1024;

    public async Task<Result<SendWhatsAppTemplateResult>> Handle(
        SendWhatsAppTemplateCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var now = clock.GetUtcNow();

        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);
        if (lead is null || lead.IsAnonymized) return Fail(WhatsAppErrors.LeadNotFound);

        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure) return Fail(access.Error!);

        var consents = await db.LeadConsents.Where(c => c.LeadId == lead.Id).ToListAsync(ct);
        var consent = WhatsAppEligibility.ConsentStateOf(consents);

        string? callingCode;
        MessagingChannelInfo? channel;

        // Other modules' stores: the ambient transaction is suppressed, or the second connection
        // promotes it to a distributed transaction Npgsql refuses.
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            callingCode = (await admin.GetSmsConfigAsync(tenantId, ct))?.DefaultCallingCode;
            channel = await integration.Messaging.ResolveChannelAsync(tenantId, lead.AgencyId, ct);
            suppressed.Complete();
        }

        var phone = WhatsAppNumbers.ToE164(lead.PhoneNumber, callingCode);
        var phoneIndex = phone is null ? null : blindIndexer.Compute(BlindIndexPurpose.Phone, phone);

        var conversation = channel is null || phoneIndex is null
            ? null
            : await db.WhatsAppConversations
                .AsTracking()
                .FirstOrDefaultAsync(
                    c => c.ConnectionId == channel.ConnectionId && c.ContactPhoneBlindIndex == phoneIndex, ct);

        var blockers = WhatsAppEligibility.Blockers(
            lead, consent, phone, channel is not null, conversation, cmd.MayForce, now,
            await Retention.ProcessingRestriction.IsRestrictedAsync(customers, tenantId, lead, ct),
            await WhatsAppTemplateCooldown.LastTemplateFailedAsync(db, conversation, ct));

        if (blockers.Count > 0) return Fail(blockers[0]);

        var template = await db.WhatsAppTemplates
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId && t.ConnectionId == channel!.ConnectionId, ct);

        if (template is null) return Fail(WhatsAppErrors.TemplateNotFound);
        if (!template.IsSendable) return Fail(WhatsAppErrors.TemplateNotApproved);
        // The product the lead ASKED for — the lead stores no line (product owner, 2026-10-24).
        if (!template.AppliesTo(await ProductLines.ProductLineLookup.ForLeadAsync(admin, lead, ct)))
            return Fail(WhatsAppErrors.TemplateNotForProduct);
        if (WhatsAppTemplateFiles.Problem(template, cmd.File, out var media) is { } fileProblem) return Fail(fileProblem);

        var values = (cmd.Variables ?? []).Select(v => v?.Trim() ?? string.Empty).ToList();

        // Meta refuses a parameter that is empty, holds a line break or a tab, or exceeds its
        // length — refused here in words rather than by the provider minutes later.
        if (values.Count != template.VariableCount
            || values.Any(v => v.Length == 0 || v.Length > MaxVariableLength || v.Contains('\n') || v.Contains('\t')))
        {
            return Fail(WhatsAppErrors.VariablesInvalid);
        }

        if (conversation is null)
        {
            conversation = WhatsAppConversation.Open(
                tenantId, channel!.ConnectionId, phone!, phoneIndex!, lead.Id, clientId: null,
                contactName: null, ambiguousMatch: false, now);

            db.WhatsAppConversations.Add(conversation);
        }
        else if (conversation.LeadId != lead.Id)
        {
            // One conversation per (institution number, customer number): the customer's reply can
            // only name the number, so it lands where the last message came from — this lead now.
            conversation.AttachToLead(lead.Id);
        }

        var message = WhatsAppMessage.OutboundTemplate(
            tenantId,
            conversation.Id,
            currentUser.Id,
            template.Id,
            template.Name,
            template.Language,
            template.Render(values),
            JsonSerializer.Serialize(values),
            now);

        if (cmd.ProjectId is { } projectId) message.LinkToProject(projectId, cmd.ProjectAttachmentId);
        var kept = await WhatsAppTemplateFiles.KeepAsync(services, message, cmd.File, media, tenantId, now, ct);
        if (kept.IsFailure) return Fail(kept.Error!);

        db.WhatsAppMessages.Add(message);

        conversation.RecordTemplateSent(now);

        // A message written to the customer is a contact ATTEMPT until they answer — their reply
        // is what establishes it (RecordMessagingEvents).
        lead.RecordContactAttempt();

        await publisher.PublishAsync(new WhatsAppMessageQueuedEvent(tenantId, message.Id), ct);

        await WhatsAppTemplateFiles.SaveAsync(db, services, message, ct);

        return Result.Ok(new SendWhatsAppTemplateResult(conversation.Id, message.Id));
    }

    private static Result<SendWhatsAppTemplateResult> Fail(string code) => Result.Fail<SendWhatsAppTemplateResult>(code);
}

/// <summary>A template's header file, as a project's route hands it over (project documents lot 3).</summary>
internal sealed record TemplateFile(byte[] Content, string ContentType, string FileName)
{
    /// <summary>Never the bytes, never the name.</summary>
    public override string ToString() => $"TemplateFile {{ {ContentType}, {Content.Length} bytes }}";
}

/// <summary>
/// A template's DOCUMENT or IMAGE header (project documents lot 3): which file it takes, and where
/// the file waits for the consumer. The send stays queued through the outbox — a provider down at
/// the click delays the devis, never loses it — so the bytes are kept in the WhatsApp media store
/// (envelope-encrypted, its own key) before the message is saved, and read back when it leaves.
/// Without that store, a file cannot be queued: refused, nothing sent.
/// </summary>
internal static class WhatsAppTemplateFiles
{
    /// <summary>The template's header wants a file and none was given — it is a project's to send.</summary>
    public const string NeedsFile = "WHATSAPP_TEMPLATE_NEEDS_FILE";
    /// <summary>A file was given for a template whose header takes none.</summary>
    public const string NoFileHeader = "WHATSAPP_TEMPLATE_HAS_NO_FILE_HEADER";
    /// <summary>No media store is configured: a file cannot wait for the consumer.</summary>
    public const string StoreUnavailable = "WHATSAPP_MEDIA_STORE_UNAVAILABLE";

    public static string? Problem(WhatsAppTemplate template, TemplateFile? file, out WhatsAppMediaKind? media)
    {
        media = null;
        if (file is null) return template.NeedsMedia ? NeedsFile : null;
        if (template.FileHeader is null) return NoFileHeader;

        var checkedFile = WhatsAppMediaSender.Check(file.Content, file.ContentType);
        if (checkedFile.IsFailure) return checkedFile.Error;
        if (!template.AcceptsFile(checkedFile.Value!.ContentType)) return WhatsAppConversationErrors.MediaTypeNotAllowed;

        media = checkedFile.Value;
        return null;
    }

    /// <summary>Stores the bytes and describes them on the message. Nothing to do without a file.</summary>
    public static async Task<Result> KeepAsync(
        IServiceProvider? services, WhatsAppMessage message, TemplateFile? file, WhatsAppMediaKind? media,
        Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        if (file is null || media is null) return Result.Ok();
        if (services?.GetService(typeof(Infrastructure.WhatsAppMedia.IWhatsAppMediaStore)) is not Infrastructure.WhatsAppMedia.IWhatsAppMediaStore store)
            return Result.Fail(StoreUnavailable);

        var stored = await store.StoreAsync(tenantId, file.Content, ct);
        message.AttachTemplateFile(media.ContentType, WhatsAppMediaSender.SafeFileName(file.FileName, media.ContentType));
        message.AttachStoredMedia(stored.StorageRef, stored.SizeBytes, stored.Sha256, now);
        return Result.Ok();
    }

    /// <summary>Saves; bytes stored for a message that could not be saved are removed rather than left behind.</summary>
    public static async Task SaveAsync(LeadsDbContext db, IServiceProvider? services, WhatsAppMessage message, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch when (message.MediaStorageRef is { } reference
                    && services?.GetService(typeof(Infrastructure.WhatsAppMedia.IWhatsAppMediaStore)) is Infrastructure.WhatsAppMedia.IWhatsAppMediaStore store)
        {
            await store.DeleteAsync(message.TenantId, reference, CancellationToken.None);
            throw;
        }
    }
}
