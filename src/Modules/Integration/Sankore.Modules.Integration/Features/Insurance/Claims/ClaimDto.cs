namespace Sankore.Modules.Integration.Features.Insurance.Claims;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One dossier, as a screen sees it.
///
/// <para>
/// <b>The account of the loss is NOT here.</b> <c>ClaimRecord.DescriptionEncrypted</c> is free
/// text about a named person's death, illness or accident, and a list of a customer's dossiers is
/// not the place for it. <see cref="HasDescription"/> says it is on file, which is what a
/// follow-up screen needs; reading it back in clear would be its own audited endpoint, and no
/// criterion of ASS-09 asks for one.
/// </para>
/// </summary>
/// <param name="MissingDocuments">
/// What the insurer is waiting for, in its own words. Shown because it IS the agent's to-do list;
/// null as soon as the insurer stops asking, which <c>ApplySync</c> enforces so a settled dossier
/// does not keep an agent chasing pièces.
/// </param>
/// <param name="IsOpen">
/// The aggregate's own definition, not a status comparison the front re-derives — the same reason
/// <c>HoldingDto.CanEdit</c> carries a server answer.
/// </param>
public sealed record ClaimDto(
    Guid Id,
    Guid ConnectionId,
    Guid PolicyId,
    Guid CrmCustomerId,
    DateOnly OccurredOn,
    string Nature,
    bool HasDescription,
    string Status,
    string? StatusDetail,
    DateTimeOffset StatusChangedAt,
    string? MissingDocuments,
    string? ExternalClaimId,
    string? ClaimNumber,
    Guid? DeclareCommandId,
    DateTimeOffset DeclaredAt,
    Guid DeclaredBy,
    decimal? IndemnityAmount,
    string? IndemnityCurrency,
    string? IndemnityCbsReference,
    DateTimeOffset? IndemnityPaidAt,
    DateTimeOffset? LastSyncedAt,
    bool IsOpen)
{
    internal static ClaimDto From(ClaimRecord c) => new(
        Id: c.Id,
        ConnectionId: c.ConnectionId,
        PolicyId: c.PolicyId,
        CrmCustomerId: c.CrmCustomerId,
        OccurredOn: c.OccurredOn,
        Nature: c.Nature,
        HasDescription: c.DescriptionEncrypted is not null,
        Status: c.Status.ToString(),
        StatusDetail: c.StatusDetail,
        StatusChangedAt: c.StatusChangedAt,
        MissingDocuments: c.MissingDocuments,
        ExternalClaimId: c.ExternalClaimId,
        ClaimNumber: c.ClaimNumber,
        DeclareCommandId: c.DeclareCommandId,
        DeclaredAt: c.DeclaredAt,
        DeclaredBy: c.DeclaredBy,
        IndemnityAmount: c.IndemnityAmount,
        IndemnityCurrency: c.IndemnityCurrency,
        IndemnityCbsReference: c.IndemnityCbsReference,
        IndemnityPaidAt: c.IndemnityPaidAt,
        LastSyncedAt: c.LastSyncedAt,
        IsOpen: c.IsOpen);
}

/// <summary>One dossier by its id.</summary>
internal sealed record GetClaimQuery(Guid ClaimId) : IRequest<Result<ClaimDto>>;

internal sealed class GetClaimHandler(IntegrationDbContext db)
    : IRequestHandler<GetClaimQuery, Result<ClaimDto>>
{
    public async Task<Result<ClaimDto>> Handle(GetClaimQuery query, CancellationToken ct)
    {
        var claim = await db.Claims.FirstOrDefaultAsync(c => c.Id == query.ClaimId, ct);

        return claim is null
            ? Result.Fail<ClaimDto>(ClaimErrors.ClaimNotFound)
            : Result.Ok(ClaimDto.From(claim));
    }
}

/// <summary>
/// A customer's dossiers, or the tenant's open ones.
///
/// <para>
/// Both in one query rather than two endpoints: the counter asks "this client's claims" and the
/// back office asks "what is still open", and the second is the first with the customer left out.
/// Splitting them would duplicate the projection and the ordering.
/// </para>
/// </summary>
internal sealed record ListClaimsQuery(Guid? CrmCustomerId, bool OpenOnly)
    : IRequest<Result<IReadOnlyList<ClaimDto>>>;

internal sealed class ListClaimsHandler(IntegrationDbContext db)
    : IRequestHandler<ListClaimsQuery, Result<IReadOnlyList<ClaimDto>>>
{
    /// <summary>
    /// A ceiling, not a pager. A customer's dossiers are units, and the open worklist of a tenant
    /// is tens — a page parameter on either would be ceremony. The cap is here so a tenant with a
    /// pathological backlog cannot make this one request hold a connection.
    /// </summary>
    private const int MaxRows = 500;

    public async Task<Result<IReadOnlyList<ClaimDto>>> Handle(
        ListClaimsQuery query, CancellationToken ct)
    {
        var rows = db.Claims.AsQueryable();

        if (query.CrmCustomerId is { } customerId)
            rows = rows.Where(c => c.CrmCustomerId == customerId);

        // The aggregate's IsOpen expressed in SQL, because the index serves it. The two must stay
        // in step: a materialised filter over the property would read the whole table.
        if (query.OpenOnly)
        {
            rows = rows.Where(c => c.Status == ClaimStatus.Declared
                                || c.Status == ClaimStatus.UnderReview
                                || c.Status == ClaimStatus.DocumentsRequired
                                || c.Status == ClaimStatus.Accepted);
        }

        var claims = await rows
            .OrderByDescending(c => c.DeclaredAt)
            .Take(MaxRows)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ClaimDto>>([.. claims.Select(ClaimDto.From)]);
    }
}
