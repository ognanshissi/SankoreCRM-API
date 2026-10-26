namespace Sankore.Modules.Customers.Features.Documents.Files;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Infrastructure.Documents;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// A file deposited on the client record (product owner, 2026-10-26). NOT an <c>ICommand</c>, on
/// purpose — like a task attachment: <c>AuditBehavior</c> serialises a command's whole payload, and
/// this one is a file. The document row is its own record of who put what, when.
/// </summary>
internal sealed record UploadClientDocument(
    Guid ClientId,
    ClientDocumentKind Kind,
    string Title,
    CustomerDocumentDirection Direction,
    DateOnly DocumentDate,
    string FileName,
    byte[] Content) : IRequest<Result<Guid>>;

/// <summary>Removal is logical and audited: the reason is part of the trace.</summary>
internal sealed record RemoveClientDocumentCommand(Guid ClientId, Guid DocumentId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

internal sealed record GetClientDocumentContentQuery(Guid ClientId, Guid DocumentId)
    : IClientReadRequest, IRequest<Result<ClientDocumentContent>>;

internal sealed record ClientDocumentContent(string FileName, string ContentType, byte[] Content);

internal sealed class ClientDocumentFileHandlers(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IServiceProvider services,
    IOptions<ClientDocumentStorageOptions> options,
    TimeProvider clock,
    ILogger<ClientDocumentFileHandlers> logger)
    : IRequestHandler<UploadClientDocument, Result<Guid>>,
      IRequestHandler<RemoveClientDocumentCommand, Result>,
      IRequestHandler<GetClientDocumentContentQuery, Result<ClientDocumentContent>>
{
    /// <summary>No key configured = no store registered: uploads are refused, not silently dropped.</summary>
    private IClientDocumentStore? Store => services.GetService<IClientDocumentStore>();

    public async Task<Result<Guid>> Handle(UploadClientDocument cmd, CancellationToken ct)
    {
        var client = await LoadClientAsync(cmd.ClientId, ct);
        if (client is null) return Result.Fail<Guid>(CustomerErrors.ClientNotFound);
        if (client.IsReadOnly) return Result.Fail<Guid>(CustomerErrors.ClientReadOnly);

        if (Store is not { } store) return Result.Fail<Guid>(CustomerErrors.ClientDocumentsUnavailable);
        if (string.IsNullOrWhiteSpace(cmd.Title)) return Result.Fail<Guid>(CustomerErrors.ClientDocumentTitleRequired);
        if (cmd.Content.Length == 0) return Result.Fail<Guid>(CustomerErrors.ClientDocumentEmpty);
        if (cmd.Content.Length > options.Value.MaxBytes) return Result.Fail<Guid>(CustomerErrors.ClientDocumentTooLarge);

        var contentType = ClientDocumentTypes.Detect(cmd.Content, cmd.FileName);
        if (contentType is null) return Result.Fail<Guid>(CustomerErrors.ClientDocumentTypeNotAllowed);

        var stored = await store.StoreAsync(client.TenantId, cmd.Content, ct);
        var document = ClientDocument.Record(
            client.TenantId, client.Id, cmd.Kind, cmd.Title, cmd.Direction, cmd.DocumentDate,
            cmd.FileName, contentType, stored.SizeBytes, stored.Sha256, stored.StorageRef,
            currentUser.Id, clock.GetUtcNow());
        db.ClientDocuments.Add(document);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Bytes nobody can reach are removed rather than left in the bucket.
            await store.DeleteAsync(client.TenantId, stored.StorageRef, CancellationToken.None);
            throw;
        }

        return Result.Ok(document.Id);
    }

    /// <summary>The file stays in the store: a logical removal hides the document, it does not destroy it.</summary>
    public async Task<Result> Handle(RemoveClientDocumentCommand cmd, CancellationToken ct)
    {
        var client = await LoadClientAsync(cmd.ClientId, ct);
        if (client is null) return Result.Fail(CustomerErrors.ClientNotFound);
        if (client.IsReadOnly) return Result.Fail(CustomerErrors.ClientReadOnly);

        var document = await db.ClientDocuments
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId && d.ClientId == cmd.ClientId, ct);
        if (document is null) return Result.Fail(CustomerErrors.ClientDocumentNotFound);

        var removed = document.Remove(currentUser.Id, cmd.Reason, clock.GetUtcNow());
        if (removed.IsFailure) return removed;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<Result<ClientDocumentContent>> Handle(GetClientDocumentContentQuery query, CancellationToken ct)
    {
        var client = await LoadClientAsync(query.ClientId, ct);
        if (client is null) return Result.Fail<ClientDocumentContent>(CustomerErrors.ClientNotFound);

        var document = await db.ClientDocuments
            .FirstOrDefaultAsync(d => d.Id == query.DocumentId && d.ClientId == query.ClientId && d.RemovedAt == null, ct);
        if (document is null || Store is not { } store)
            return Result.Fail<ClientDocumentContent>(CustomerErrors.ClientDocumentNotFound);

        var content = await store.OpenAsync(client.TenantId, document.StorageRef, ct);
        if (content is null)
        {
            logger.LogWarning("Client document {DocumentId} has a row but no readable object", document.Id);
            return Result.Fail<ClientDocumentContent>(CustomerErrors.ClientDocumentNotFound);
        }

        return Result.Ok(new ClientDocumentContent(document.FileName, document.ContentType, content));
    }

    /// <summary>
    /// The client, inside the caller's agency perimeter — outside it reads as not found, never as
    /// forbidden, so a client's existence does not leak.
    /// </summary>
    private async Task<Client?> LoadClientAsync(Guid clientId, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null) return null;

        return await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct)
            ? client
            : null;
    }
}
