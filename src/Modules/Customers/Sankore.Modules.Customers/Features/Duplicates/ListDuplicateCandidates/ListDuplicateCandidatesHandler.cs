namespace Sankore.Modules.Customers.Features.Duplicates.ListDuplicateCandidates;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Pages the duplicate review queue, highest score first.
/// <para>
/// Agency perimeter: a candidate is only listed when BOTH clients are inside the caller's perimeter.
/// Anything else would show half a pair the reviewer cannot act on — a merge needs both records —
/// and would leak the existence of a client from another branch. A <c>null</c> perimeter means
/// super-user: no filter at all.
/// </para>
/// </summary>
internal sealed class ListDuplicateCandidatesHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<ListDuplicateCandidatesQuery, Result<PagedResult<DuplicateCandidateDto>>>
{
    public async Task<Result<PagedResult<DuplicateCandidateDto>>> Handle(
        ListDuplicateCandidatesQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, currentUser.Id, ct);

        var clients = db.Clients.IgnoreQueryFilters().Where(c => c.TenantId == tenantId);

        var rows = from d in db.DuplicateCandidates.IgnoreQueryFilters().Where(d => d.TenantId == tenantId)
                   join a in clients on d.ClientAId equals a.Id
                   join b in clients on d.ClientBId equals b.Id
                   select new { Candidate = d, A = a, B = b };

        if (query.Status is not null)
            rows = rows.Where(x => x.Candidate.Status == query.Status);

        if (accessible is not null)
        {
            var agencyIds = accessible.ToList();
            rows = rows.Where(x => agencyIds.Contains(x.A.AgencyId) && agencyIds.Contains(x.B.AgencyId));
        }

        var total = await rows.CountAsync(ct);

        var materialized = await rows
            .OrderByDescending(x => x.Candidate.Score)
            .ThenByDescending(x => x.Candidate.DetectedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = materialized
            .Select(x => new DuplicateCandidateDto(
                x.Candidate.Id,
                x.Candidate.Score,
                x.Candidate.Status.ToString(),
                DeserializeReasons(x.Candidate.ReasonsJson),
                x.Candidate.DetectedAt,
                x.Candidate.ReviewedBy,
                x.Candidate.ReviewedAt,
                ToDto(x.A),
                ToDto(x.B)))
            .ToList();

        return Result.Ok(new PagedResult<DuplicateCandidateDto>(items, total, page, pageSize));
    }

    private static DuplicateCandidateClientDto ToDto(Domain.Client client) =>
        new(client.Id,
            client.ClientNumber,
            client.DisplayName,
            client.Type.ToString(),
            client.Status.ToString(),
            client.AgencyId,
            client.AgencyCode,
            client.KycStatus.ToString());

    /// <summary>
    /// Reasons are stored as jsonb written by the detection handler. A row hand-edited in the
    /// database must not take the review screen down, so a parse failure degrades to "no reason".
    /// </summary>
    private static List<DuplicateReasonDto> DeserializeReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<DuplicateReasonDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
