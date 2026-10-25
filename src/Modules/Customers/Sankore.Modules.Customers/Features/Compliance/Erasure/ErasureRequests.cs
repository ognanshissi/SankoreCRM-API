namespace Sankore.Modules.Customers.Features.Compliance.Erasure;

using System.Transactions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

// ── Contracts ──────────────────────────────────────────────────────────────

public sealed record ErasureRequestDto(
    Guid Id,
    ErasureSubjectType SubjectType,
    Guid SubjectId,
    string SubjectReference,
    Guid? ClientId,
    string Reason,
    string Evidence,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    ErasureRequestStatus Status,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    string? Blocker,
    DateTimeOffset? EligibleFrom,
    DateTimeOffset? CompletedAt);

/// <param name="IsClient">The person is a client: their legal retention applies.</param>
/// <param name="Blocker">Why it cannot be erased today; null when it can.</param>
/// <param name="EligibleFrom">When it becomes possible, if known.</param>
public sealed record ErasureEligibilityDto(
    bool Eligible, bool AlreadyErased, string Reference, bool IsClient, string? Blocker, DateTimeOffset? EligibleFrom);

internal static class ErasureMapping
{
    public static ErasureRequestDto ToDto(ErasureRequest r) => new(
        r.Id, r.SubjectType, r.SubjectId, r.SubjectReference, r.ClientId, r.Reason, r.Evidence,
        r.RequestedBy, r.RequestedAt, r.Status, r.DecidedBy, r.DecidedAt, r.DecisionNote,
        r.Blocker, r.EligibleFrom, r.CompletedAt);

    public static ErasureEligibilityDto ToDto(ErasureVerdict v) => new(
        v.Eligible, v.AlreadyErased, v.Reference, v.ClientId is not null, v.Blocker, v.EligibleFrom);
}

/// <summary>
/// The requester may only name a person they can reach: a client of their agency perimeter, a
/// lead they may work. Anything else answers « not found », never « forbidden » — a refusal
/// would confirm the person exists.
/// </summary>
internal sealed class ErasurePerimeter(ICurrentUser currentUser, IAgencyScopeProvider agencyScope, CustomersDbContext db, ILeadsModule? leads = null)
{
    public async Task<bool> CanReachAsync(ErasureSubjectType type, Guid subjectId, CancellationToken ct)
    {
        if (type == ErasureSubjectType.Client)
        {
            var agencyId = await db.Clients.Where(c => c.Id == subjectId).Select(c => (Guid?)c.AgencyId).FirstOrDefaultAsync(ct);
            return agencyId is { } agency && await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, agency, ct);
        }

        if (leads is null) return false;
        using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        return await leads.CanActorWorkLeadAsync(currentUser.TenantId, subjectId, currentUser.Id, ct);
    }
}

// ── Preview ────────────────────────────────────────────────────────────────

public sealed record GetErasureEligibilityQuery(ErasureSubjectType SubjectType, Guid SubjectId)
    : IRequest<Result<ErasureEligibilityDto>>;

internal sealed class GetErasureEligibilityHandler(
    ICurrentUser currentUser, ErasurePerimeter perimeter, ErasureEligibility eligibility)
    : IRequestHandler<GetErasureEligibilityQuery, Result<ErasureEligibilityDto>>
{
    public async Task<Result<ErasureEligibilityDto>> Handle(GetErasureEligibilityQuery q, CancellationToken ct)
    {
        if (!await perimeter.CanReachAsync(q.SubjectType, q.SubjectId, ct))
            return Result.Fail<ErasureEligibilityDto>(CustomerErrors.ErasureSubjectNotFound);

        var verdict = await eligibility.EvaluateAsync(currentUser.TenantId, q.SubjectType, q.SubjectId, ct);
        return verdict.Found
            ? Result.Ok(ErasureMapping.ToDto(verdict))
            : Result.Fail<ErasureEligibilityDto>(CustomerErrors.ErasureSubjectNotFound);
    }
}

// ── Request ────────────────────────────────────────────────────────────────

public sealed record CreateErasureRequestCommand(
    ErasureSubjectType SubjectType, Guid SubjectId, string Reason, string Evidence)
    : IRequest<Result<ErasureRequestDto>>, ICommand, IResourceCommand
{
    public string ResourceType => SubjectType.ToString();
    public string? ResourceId => SubjectId.ToString();
}

internal sealed class CreateErasureRequestValidator : AbstractValidator<CreateErasureRequestCommand>
{
    public CreateErasureRequestValidator()
    {
        RuleFor(x => x.SubjectType).IsInEnum();
        RuleFor(x => x.SubjectId).NotEmpty();
        // Both outlive the erasure: they must say why and on what basis, not who.
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(ErasureRequest.MaxTextLength);
        RuleFor(x => x.Evidence).NotEmpty().MinimumLength(5).MaximumLength(ErasureRequest.MaxTextLength);
    }
}

internal sealed class CreateErasureRequestHandler(
    CustomersDbContext db, ICurrentUser currentUser, ErasurePerimeter perimeter, ErasureEligibility eligibility, TimeProvider clock)
    : IRequestHandler<CreateErasureRequestCommand, Result<ErasureRequestDto>>
{
    public async Task<Result<ErasureRequestDto>> Handle(CreateErasureRequestCommand cmd, CancellationToken ct)
    {
        if (!await perimeter.CanReachAsync(cmd.SubjectType, cmd.SubjectId, ct))
            return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureSubjectNotFound);

        var verdict = await eligibility.EvaluateAsync(currentUser.TenantId, cmd.SubjectType, cmd.SubjectId, ct);
        if (!verdict.Found) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureSubjectNotFound);
        if (verdict.AlreadyErased) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureAlreadyDone);

        var open = await db.ErasureRequests.AnyAsync(r =>
            r.SubjectType == cmd.SubjectType && r.SubjectId == cmd.SubjectId
            && (r.Status == ErasureRequestStatus.Pending || r.Status == ErasureRequestStatus.Restricted), ct);
        if (open) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureAlreadyRequested);

        var request = ErasureRequest.Create(
            currentUser.TenantId, cmd.SubjectType, cmd.SubjectId, verdict.Reference, verdict.ClientId,
            cmd.Reason, cmd.Evidence, currentUser.Id, clock.GetUtcNow());
        db.ErasureRequests.Add(request);
        await db.SaveChangesAsync(ct);

        return Result.Ok(ErasureMapping.ToDto(request));
    }
}

// ── Decision ───────────────────────────────────────────────────────────────

public sealed record ApproveErasureRequestCommand(Guid RequestId, string? Note)
    : IRequest<Result<ErasureRequestDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "ErasureRequest";
    public string? ResourceId => RequestId.ToString();
}

/// <summary>
/// The second person's yes. Erased now when the person can be; otherwise the processing of the
/// client is restricted and the erasure waits for <see cref="ProcessRestrictedErasuresJob"/>.
/// </summary>
internal sealed class ApproveErasureRequestHandler(
    CustomersDbContext db, ICurrentUser currentUser, ErasureEligibility eligibility, ErasureExecutor executor, TimeProvider clock)
    : IRequestHandler<ApproveErasureRequestCommand, Result<ErasureRequestDto>>
{
    public async Task<Result<ErasureRequestDto>> Handle(ApproveErasureRequestCommand cmd, CancellationToken ct)
    {
        var request = await db.ErasureRequests.AsTracking().FirstOrDefaultAsync(r => r.Id == cmd.RequestId, ct);
        if (request is null) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureNotFound);

        var allowed = request.CanBeDecidedBy(currentUser.Id);
        if (allowed.IsFailure) return Result.Fail<ErasureRequestDto>(allowed.Error!);

        var now = clock.GetUtcNow();
        var verdict = await eligibility.EvaluateAsync(request.TenantId, request.SubjectType, request.SubjectId, ct);
        if (!verdict.Found) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureSubjectNotFound);

        if (verdict.Eligible)
        {
            await executor.ExecuteAsync(request, verdict, currentUser.Id, ct);
            request.Complete(currentUser.Id, now, cmd.Note);
        }
        else if (verdict.AlreadyErased)
        {
            // Erased meanwhile — by the retention job, or a client's anonymisation: nothing left to do.
            request.Complete(currentUser.Id, now, cmd.Note);
        }
        else
        {
            request.Restrict(currentUser.Id, now, cmd.Note, verdict.Blocker!, verdict.EligibleFrom);
            if (verdict.ClientId is { } clientId)
            {
                var client = await db.Clients.AsTracking().FirstAsync(c => c.Id == clientId, ct);
                client.RestrictProcessing(now);
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(ErasureMapping.ToDto(request));
    }
}

public sealed record RejectErasureRequestCommand(Guid RequestId, string Note)
    : IRequest<Result<ErasureRequestDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "ErasureRequest";
    public string? ResourceId => RequestId.ToString();
}

internal sealed class RejectErasureRequestValidator : AbstractValidator<RejectErasureRequestCommand>
{
    public RejectErasureRequestValidator()
        => RuleFor(x => x.Note).NotEmpty().MinimumLength(5).MaximumLength(ErasureRequest.MaxTextLength);
}

internal sealed class RejectErasureRequestHandler(CustomersDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IRequestHandler<RejectErasureRequestCommand, Result<ErasureRequestDto>>
{
    public async Task<Result<ErasureRequestDto>> Handle(RejectErasureRequestCommand cmd, CancellationToken ct)
    {
        var request = await db.ErasureRequests.AsTracking().FirstOrDefaultAsync(r => r.Id == cmd.RequestId, ct);
        if (request is null) return Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureNotFound);

        var allowed = request.CanBeDecidedBy(currentUser.Id);
        if (allowed.IsFailure) return Result.Fail<ErasureRequestDto>(allowed.Error!);

        request.Reject(currentUser.Id, clock.GetUtcNow(), cmd.Note);
        await db.SaveChangesAsync(ct);
        return Result.Ok(ErasureMapping.ToDto(request));
    }
}

// ── Reading ────────────────────────────────────────────────────────────────

public sealed record ListErasureRequestsQuery(ErasureRequestStatus? Status, int Page = 1, int PageSize = 25)
    : IRequest<Result<PagedResult<ErasureRequestDto>>>;

/// <summary>
/// Who reads which request. The tenant is the query filter's. Within it, the deciders
/// (<c>privacy:erasure:approve</c>, compliance — tenant-wide by design) read every request; anybody
/// else reads only the ones they recorded: a sales manager of one agency has no business reading
/// another agency's client numbers and the reasons people gave.
/// </summary>
internal static class ErasureReadScope
{
    public static IQueryable<ErasureRequest> For(IQueryable<ErasureRequest> requests, ICurrentUser user)
        => user.Permissions.Contains(Permissions.CanApproveErasure.Code)
            ? requests
            : requests.Where(r => r.RequestedBy == user.Id);
}

internal sealed class ListErasureRequestsHandler(CustomersDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ListErasureRequestsQuery, Result<PagedResult<ErasureRequestDto>>>
{
    public async Task<Result<PagedResult<ErasureRequestDto>>> Handle(ListErasureRequestsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);

        var query = ErasureReadScope.For(db.ErasureRequests, currentUser);
        if (q.Status is { } status) query = query.Where(r => r.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            // Pending first: they are the queue; then the most recent.
            .OrderBy(r => r.Status == ErasureRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.RequestedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<ErasureRequestDto>(items.Select(ErasureMapping.ToDto).ToList(), total, page, size));
    }
}

public sealed record GetErasureRequestQuery(Guid RequestId) : IRequest<Result<ErasureRequestDto>>;

internal sealed class GetErasureRequestHandler(CustomersDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetErasureRequestQuery, Result<ErasureRequestDto>>
{
    public async Task<Result<ErasureRequestDto>> Handle(GetErasureRequestQuery q, CancellationToken ct)
    {
        // Not found, not forbidden: a refusal would confirm the request exists.
        var request = await ErasureReadScope.For(db.ErasureRequests, currentUser)
            .FirstOrDefaultAsync(r => r.Id == q.RequestId, ct);
        return request is null
            ? Result.Fail<ErasureRequestDto>(CustomerErrors.ErasureNotFound)
            : Result.Ok(ErasureMapping.ToDto(request));
    }
}
