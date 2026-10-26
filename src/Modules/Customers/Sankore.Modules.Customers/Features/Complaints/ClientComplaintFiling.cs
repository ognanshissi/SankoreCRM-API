namespace Sankore.Modules.Customers.Features.Complaints;

using System.Globalization;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>What a complaint is recorded from, whoever records it.</summary>
internal sealed record ClientComplaintFilingInput(
    Client Client,
    ClientComplaintChannel Channel,
    ClientComplaintCategory Category,
    string Subject,
    string Description,
    Guid? HoldingId,
    Guid ReceivedBy,
    Guid? SourceConversationId);

/// <summary>
/// The one way a complaint enters the register — typed by an agent or filed by the WhatsApp bot
/// (2026-10-26). One body so the two cannot drift on the deadlines, the assignee or the alert:
/// a bot complaint nobody is told about would be the worst outcome of the feature.
/// </summary>
internal sealed class ClientComplaintFiling(
    CustomersDbContext db,
    IFieldEncryptor encryptor,
    ICustomerSettings settings,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<ClientComplaintFiling> logger,
    IAdministrationModule? administration = null)
{
    internal const string ReferencePrefix = "RCL";

    /// <summary>Saves the complaint and queues its alert in the same transaction (the outbox writer does not save).</summary>
    public async Task<Result<ClientComplaint>> FileAsync(ClientComplaintFilingInput input, CancellationToken ct)
    {
        var client = input.Client;
        if (client.IsReadOnly) return Result.Fail<ClientComplaint>(CustomerErrors.ClientReadOnly);
        if (string.IsNullOrWhiteSpace(input.Subject)) return Result.Fail<ClientComplaint>(CustomerErrors.ComplaintSubjectRequired);
        if (string.IsNullOrWhiteSpace(input.Description)) return Result.Fail<ClientComplaint>(CustomerErrors.ComplaintDescriptionRequired);

        if (input.HoldingId is { } holdingId
            && !await db.ProductHoldings.IgnoreQueryFilters()
                .AnyAsync(h => h.TenantId == client.TenantId && h.Id == holdingId && h.ClientId == client.Id, ct))
            return Result.Fail<ClientComplaint>(CustomerErrors.ComplaintHoldingNotOfClient);

        var acknowledgementDays = await settings.GetIntAsync(client.TenantId, CustomerSettingKeys.ComplaintAcknowledgementDays, ct);
        var resolutionDays = await settings.GetIntAsync(client.TenantId, CustomerSettingKeys.ComplaintResolutionDays, ct);

        var now = clock.GetUtcNow();
        var complaint = ClientComplaint.Record(
            client.TenantId,
            await NextReferenceAsync(client.TenantId, now.Year, ct),
            client.Id,
            client.AgencyId,
            input.HoldingId,
            input.Channel,
            input.Category,
            input.Subject,
            encryptor.Encrypt(input.Description.Trim()),
            input.ReceivedBy,
            input.SourceConversationId,
            await ResolveAssigneeAsync(client, ct),
            now,
            acknowledgementDays,
            // Two settings an administrator moves independently: an answer due before its
            // acknowledgement is a misconfiguration, read as « both on the same day ».
            Math.Max(resolutionDays, acknowledgementDays));

        db.ClientComplaints.Add(complaint);

        if (complaint.AssignedTo is { } assignee)
        {
            await publisher.PublishAsync(new ClientComplaintActionRequiredEvent(
                complaint.TenantId, complaint.ClientId, complaint.Id, complaint.Reference, complaint.Subject,
                assignee, ClientComplaintActionReasons.Filed, complaint.AcknowledgementDueAt), ct);
        }
        else
        {
            // Still recorded: the register is where it waits, and the deadline sweep will reach
            // the manager the day one is named.
            logger.LogWarning(
                "Complaint {Reference} of client {ClientId}: nobody to assign (no advisor, no creator, no agency manager).",
                complaint.Reference, client.Id);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(complaint);
    }

    /// <summary>
    /// The advisor, else whoever created the client, accepted only while active and attached to
    /// the client's agency — else that agency's manager. Same cascade as M02's remote files.
    /// </summary>
    private async Task<Guid?> ResolveAssigneeAsync(Client client, CancellationToken ct)
    {
        var preferred = client.AdvisorUserId ?? (client.CreatedBy == Guid.Empty ? null : client.CreatedBy);
        if (administration is null) return preferred;

        try
        {
            // Another module's DbContext under this command's transaction would promote it to a
            // distributed one, which Npgsql refuses.
            using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            return await administration.ResolveAgencyRecipientAsync(client.TenantId, client.AgencyId, preferred, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // M12 down must not lose the complaint: the unchecked first choice is better than nobody.
            logger.LogWarning(ex, "Complaint assignee of client {ClientId} not checked; the advisor is kept.", client.Id);
            return preferred;
        }
    }

    /// <summary>
    /// <c>RCL-2026-000042</c>. Two recordings at the same instant can compute the same number; the
    /// unique (tenant, reference) index makes the second fail rather than share it.
    /// </summary>
    private async Task<string> NextReferenceAsync(Guid tenantId, int year, CancellationToken ct)
    {
        var prefix = $"{ReferencePrefix}-{year}-";

        var highest = await db.ClientComplaints
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Reference.StartsWith(prefix))
            .OrderByDescending(c => c.Reference)
            .Select(c => c.Reference)
            .FirstOrDefaultAsync(ct);

        var next = highest is { Length: > 0 }
                   && int.TryParse(highest.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed + 1
            : 1;

        // Six digits, zero-padded: the string order is the numeric order the query above relies on.
        return $"{prefix}{next:D6}";
    }
}
