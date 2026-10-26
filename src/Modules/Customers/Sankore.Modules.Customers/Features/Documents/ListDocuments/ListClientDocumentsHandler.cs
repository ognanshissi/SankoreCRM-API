namespace Sankore.Modules.Customers.Features.Documents.ListDocuments;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Asks each module that keeps a client's documents for its list, and folds them into one.
///
/// <para>
/// <b>Every source is listed whatever the caller's permissions</b> — the product owner chose to
/// show that a document exists even to somebody who may not open it (2026-10-26). What a missing
/// permission withholds is the file name and the path; the source's endpoint still checks the
/// permission, the perimeter and its access log when the file is opened.
/// </para>
///
/// <para>
/// Insurance is read only when the tenant has the module (in force, or deactivated and kept for
/// consultation). A module that fails leaves the others standing and is named in
/// <see cref="ClientDocumentsDto.UnavailableSources"/>.
/// </para>
/// </summary>
internal sealed class ListClientDocumentsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    ITenantModules modules,
    ILogger<ListClientDocumentsHandler> logger,
    IKycModule? kyc = null,
    IIntegrationModule? integration = null,
    ILeadsModule? leads = null,
    INotificationsModule? notifications = null)
    : IRequestHandler<ListClientDocumentsQuery, Result<ClientDocumentsDto>>
{
    internal const string KycSource = "Kyc";
    internal const string InsuranceSource = "Insurance";
    internal const string LeadsSource = "Leads";
    internal const string ClientSource = "Client";
    internal const string MessagesSource = "Messages";

    public async Task<Result<ClientDocumentsDto>> Handle(ListClientDocumentsQuery query, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == query.ClientId)
            .Select(c => new { c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientDocumentsDto>(CustomerErrors.ClientNotFound);

        var tenantId = currentUser.TenantId;
        var unavailable = new List<string>();
        var entries = new List<CustomerDocumentEntry>();

        if (kyc is not null)
            entries.AddRange(await ReadAsync(KycSource, unavailable,
                () => kyc.ListCustomerDocumentsAsync(tenantId, query.ClientId, ct)));

        if (integration is not null && await modules.IsReadableAsync(TenantModules.Insurance, ct))
            entries.AddRange(await ReadAsync(InsuranceSource, unavailable,
                () => integration.Insurance.ListCustomerDocumentsAsync(query.ClientId, ct)));

        if (leads is not null)
            entries.AddRange(await ReadAsync(LeadsSource, unavailable,
                () => leads.ListCustomerDocumentsAsync(tenantId, query.ClientId, ct)));

        // The e-mails and SMS the client was sent: a « delivered » line with no file to open.
        if (notifications is not null)
            entries.AddRange(await ReadAsync(MessagesSource, unavailable, async () =>
                (await notifications.ListClientMessagesAsync(tenantId, query.ClientId, ct))
                    .Select(m => new CustomerDocumentEntry(
                        Key: $"message:{m.MessageId}",
                        Source: "Message",
                        Direction: CustomerDocumentDirection.Delivered,
                        Kind: $"Message.{m.Channel}",
                        FileName: null,
                        ContentType: null,
                        SizeBytes: null,
                        At: m.SentAt ?? m.CreatedAt,
                        Status: m.Status,
                        Detail: m.TemplateKey,
                        OriginType: "Client",
                        OriginId: query.ClientId,
                        OriginLabel: m.Recipient,
                        ContentPath: null,
                        OpenPermissions: []))
                    .ToList()));

        // This module's own: the files filed on the record, a removed one left out.
        var filed = await db.ClientDocuments
            .Where(d => d.ClientId == query.ClientId && d.RemovedAt == null)
            .ToListAsync(ct);
        entries.AddRange(filed.Select(d => new CustomerDocumentEntry(
            Key: $"{ClientSource}:{d.Id}",
            Source: ClientSource,
            Direction: d.Direction,
            Kind: $"ClientDocument.{d.Kind}",
            FileName: d.FileName,
            ContentType: d.ContentType,
            SizeBytes: d.SizeBytes,
            // The document's own date, which is what an agent sorts a record's letters by.
            At: new DateTimeOffset(d.DocumentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Status: null,
            Detail: d.Title,
            OriginType: "Client",
            OriginId: d.ClientId,
            OriginLabel: "Fiche client",
            ContentPath: $"clients/{d.ClientId}/documents/{d.Id}/content",
            OpenPermissions: [Permissions.CanReadCustomer.Code])));

        var granted = currentUser.Permissions.ToHashSet(StringComparer.Ordinal);
        var mayRemove = granted.Contains(Permissions.CanUpdateCustomer.Code);

        var items = entries
            .OrderByDescending(e => e.At)
            .Select(e =>
            {
                var canOpen = e.ContentPath is not null
                           && (e.OpenPermissions.Count == 0 || e.OpenPermissions.Any(granted.Contains));

                return new ClientDocumentDto(
                    Key: e.Key,
                    Source: e.Source,
                    Direction: e.Direction.ToString(),
                    Kind: e.Kind,
                    FileName: canOpen ? e.FileName : null,
                    ContentType: e.ContentType,
                    SizeBytes: e.SizeBytes,
                    At: e.At,
                    Status: e.Status,
                    Detail: e.Detail,
                    OriginType: e.OriginType,
                    OriginId: e.OriginId,
                    OriginLabel: e.OriginLabel,
                    ContentPath: canOpen ? e.ContentPath : null,
                    CanOpen: canOpen,
                    CanRemove: mayRemove && e.Source == ClientSource);
            })
            .ToList();

        return Result.Ok(new ClientDocumentsDto(items, unavailable));
    }

    private async Task<IReadOnlyList<CustomerDocumentEntry>> ReadAsync(
        string source, List<string> unavailable, Func<Task<IReadOnlyList<CustomerDocumentEntry>>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Client documents: the {Source} source could not be read", source);
            unavailable.Add(source);
            return [];
        }
    }
}
