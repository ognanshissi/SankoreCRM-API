namespace Sankore.Modules.Customers.Features.Complaints;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

// ── Contracts ───────────────────────────────────────────────────────────────

public sealed record ClientComplaintSummaryDto(
    Guid Id,
    string Reference,
    string Channel,
    string Category,
    string Subject,
    string Status,
    DateTimeOffset ReceivedAt,
    Guid? AssignedTo,
    DateTimeOffset AcknowledgementDueAt,
    DateTimeOffset ResolutionDueAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    /// <summary>An open complaint past one of its deadlines.</summary>
    bool IsOverdue,
    /// <summary>Recorded by the WhatsApp bot (no agent behind it).</summary>
    bool FiledBySystem);

/// <summary>The summary plus what the client said and what they were told, decrypted.</summary>
public sealed record ClientComplaintDetailDto(
    ClientComplaintSummaryDto Summary,
    Guid? HoldingId,
    Guid ReceivedBy,
    Guid? SourceConversationId,
    string? Description,
    string? Response,
    Guid? AcknowledgedBy,
    Guid? ResolvedBy,
    Guid? ClosedBy,
    string? ClosureReason);

/// <summary>
/// An agent records a complaint. The description is free text about a person's money: masked in
/// the audit trail (<see cref="SensitiveDataAttribute"/>), encrypted in the row.
/// </summary>
internal sealed record FileClientComplaintCommand(
    Guid ClientId,
    ClientComplaintChannel Channel,
    ClientComplaintCategory Category,
    string Subject,
    [property: SensitiveData] string Description,
    Guid? HoldingId) : IRequest<Result<FileClientComplaintResponse>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

public sealed record FileClientComplaintResponse(Guid ComplaintId, string Reference);

internal sealed record AcknowledgeClientComplaintCommand(Guid ClientId, Guid ComplaintId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

internal sealed record ResolveClientComplaintCommand(Guid ClientId, Guid ComplaintId, [property: SensitiveData] string Response)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

internal sealed record CloseClientComplaintCommand(Guid ClientId, Guid ComplaintId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

internal sealed record ListClientComplaintsQuery(Guid ClientId)
    : IClientReadRequest, IRequest<Result<IReadOnlyList<ClientComplaintSummaryDto>>>;

internal sealed record GetClientComplaintQuery(Guid ClientId, Guid ComplaintId)
    : IClientReadRequest, IRequest<Result<ClientComplaintDetailDto>>;

// ── Handlers ────────────────────────────────────────────────────────────────

internal sealed class ClientComplaintHandlers(
    CustomersDbContext db,
    ClientComplaintFiling filing,
    IFieldEncryptor encryptor,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher)
    : IRequestHandler<FileClientComplaintCommand, Result<FileClientComplaintResponse>>,
      IRequestHandler<AcknowledgeClientComplaintCommand, Result>,
      IRequestHandler<ResolveClientComplaintCommand, Result>,
      IRequestHandler<CloseClientComplaintCommand, Result>,
      IRequestHandler<ListClientComplaintsQuery, Result<IReadOnlyList<ClientComplaintSummaryDto>>>,
      IRequestHandler<GetClientComplaintQuery, Result<ClientComplaintDetailDto>>
{
    public async Task<Result<FileClientComplaintResponse>> Handle(FileClientComplaintCommand cmd, CancellationToken ct)
    {
        var client = await LoadClientAsync(cmd.ClientId, ct);
        if (client is null) return Result.Fail<FileClientComplaintResponse>(CustomerErrors.ClientNotFound);

        var filed = await filing.FileAsync(new ClientComplaintFilingInput(
            client, cmd.Channel, cmd.Category, cmd.Subject, cmd.Description, cmd.HoldingId,
            currentUser.Id, SourceConversationId: null), ct);

        return filed.IsSuccess
            ? Result.Ok(new FileClientComplaintResponse(filed.Value!.Id, filed.Value.Reference))
            : Result.Fail<FileClientComplaintResponse>(filed.Error!);
    }

    public Task<Result> Handle(AcknowledgeClientComplaintCommand cmd, CancellationToken ct)
        => ChangeAsync(cmd.ClientId, cmd.ComplaintId, ClientComplaintStages.Acknowledged,
            c => c.Acknowledge(currentUser.Id, clock.GetUtcNow()), ct);

    public Task<Result> Handle(ResolveClientComplaintCommand cmd, CancellationToken ct)
        => ChangeAsync(cmd.ClientId, cmd.ComplaintId, ClientComplaintStages.Resolved,
            c => c.Resolve(currentUser.Id, string.IsNullOrWhiteSpace(cmd.Response) ? null : encryptor.Encrypt(cmd.Response.Trim()), clock.GetUtcNow()), ct);

    public Task<Result> Handle(CloseClientComplaintCommand cmd, CancellationToken ct)
        => ChangeAsync(cmd.ClientId, cmd.ComplaintId, ClientComplaintStages.Closed,
            c => c.Close(currentUser.Id, cmd.Reason, clock.GetUtcNow()), ct);

    public async Task<Result<IReadOnlyList<ClientComplaintSummaryDto>>> Handle(ListClientComplaintsQuery query, CancellationToken ct)
    {
        if (await LoadClientAsync(query.ClientId, ct) is null)
            return Result.Fail<IReadOnlyList<ClientComplaintSummaryDto>>(CustomerErrors.ClientNotFound);

        var complaints = await db.ClientComplaints
            .Where(c => c.ClientId == query.ClientId)
            .OrderByDescending(c => c.ReceivedAt)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        return Result.Ok<IReadOnlyList<ClientComplaintSummaryDto>>(complaints.Select(c => ToSummary(c, now)).ToList());
    }

    public async Task<Result<ClientComplaintDetailDto>> Handle(GetClientComplaintQuery query, CancellationToken ct)
    {
        if (await LoadClientAsync(query.ClientId, ct) is null)
            return Result.Fail<ClientComplaintDetailDto>(CustomerErrors.ClientNotFound);

        var c = await db.ClientComplaints
            .FirstOrDefaultAsync(x => x.Id == query.ComplaintId && x.ClientId == query.ClientId, ct);
        if (c is null) return Result.Fail<ClientComplaintDetailDto>(CustomerErrors.ComplaintNotFound);

        return Result.Ok(new ClientComplaintDetailDto(
            ToSummary(c, clock.GetUtcNow()), c.HoldingId, c.ReceivedBy, c.SourceConversationId,
            encryptor.Decrypt(c.BodyEncrypted), encryptor.Decrypt(c.ResponseEncrypted),
            c.AcknowledgedBy, c.ResolvedBy, c.ClosedBy, c.ClosureReason));
    }

    internal static ClientComplaintSummaryDto ToSummary(ClientComplaint c, DateTimeOffset now) => new(
        c.Id, c.Reference, c.Channel.ToString(), c.Category.ToString(), c.Subject, c.Status.ToString(),
        c.ReceivedAt, c.AssignedTo, c.AcknowledgementDueAt, c.ResolutionDueAt,
        c.AcknowledgedAt, c.ResolvedAt, c.ClosedAt,
        IsOverdue: c.IsOpen && (now > c.ResolutionDueAt || (c.AcknowledgedAt is null && now > c.AcknowledgementDueAt)),
        FiledBySystem: c.ReceivedBy == Guid.Empty);

    private async Task<Result> ChangeAsync(
        Guid clientId, Guid complaintId, string stage, Func<ClientComplaint, Result> change, CancellationToken ct)
    {
        var client = await LoadClientAsync(clientId, ct);
        if (client is null) return Result.Fail(CustomerErrors.ClientNotFound);

        var complaint = await db.ClientComplaints
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == complaintId && c.ClientId == clientId, ct);
        if (complaint is null) return Result.Fail(CustomerErrors.ComplaintNotFound);

        var result = change(complaint);
        if (result.IsFailure) return result;

        // Through the outbox, in the same save: the tasks M13 opened for this complaint close with it.
        await publisher.PublishAsync(
            new ClientComplaintProgressedEvent(complaint.TenantId, complaint.ClientId, complaint.Id, stage), ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>
    /// The client inside the caller's agency perimeter — outside it reads as not found, never as
    /// forbidden, so a client's existence does not leak. The assigned-only rule is the
    /// <see cref="ClientVisibilityBehavior{TRequest,TResponse}"/>'s, on the queries.
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
