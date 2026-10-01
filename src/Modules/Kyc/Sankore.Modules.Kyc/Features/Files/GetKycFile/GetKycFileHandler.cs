namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetKycFileHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<GetKycFileQuery, Result<KycFileDto>>
{
    public async Task<Result<KycFileDto>> Handle(GetKycFileQuery request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant; a file of another tenant is
        // simply absent, and the caller is told NOT_FOUND rather than FORBIDDEN. Same rule as M01:
        // the existence of a record must not leak across tenants.
        var query = db.KycFiles.AsQueryable();

        query = request.KycFileId is { } id
            ? query.Where(f => f.Id == id)
            // By customer: the open file is the one a screen means. Closed files stay as evidence
            // and are reached by id.
            : query.Where(f => f.CustomerId == request.CustomerId
                            && f.Status != KycFileStatus.Rejected
                            && f.Status != KycFileStatus.Suspended);

        // Agency perimeter. Until now this read answered for every agency of the tenant, so a
        // counter clerk who got hold of a file id saw another branch's compliance file. Applied as a
        // predicate and reported as NOT_FOUND, never FORBIDDEN — the same rule as M01: a 403 would
        // confirm the file exists, which is the one thing the perimeter is meant to hide.
        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (perimeter is not null)
        {
            // Materialised as a List, deliberately: on .NET 10 an array's Contains binds to the
            // ReadOnlySpan<T> extension and no longer translates to SQL. A List does.
            var accessible = perimeter.ToList();

            // A file with no agency is invisible to a restricted caller — see KycFile.AgencyId.
            // Failing closed: an unresolved agency must not become "everyone's".
            query = query.Where(f => f.AgencyId != null && accessible.Contains(f.AgencyId.Value));
        }

        var dto = await query
            .OrderByDescending(f => f.UpdatedAt)
            .Select(f => new KycFileDto(
                f.Id,
                f.CustomerId,
                f.Status.ToString(),
                f.Tier.ToString(),
                f.Channel.ToString(),
                f.VigilanceLevel.ToString(),
                f.ConfidenceScore,
                f.ConfidenceLevel == null ? null : f.ConfidenceLevel.ToString(),
                f.DuplicateSuspected,
                f.FaceMatchAttempts,
                f.NextReviewDate,
                f.ValidatedAt,
                f.CreatedAt,
                f.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return dto is null
            ? Result.Fail<KycFileDto>(KycErrors.FileNotFound)
            : Result.Ok(dto);
    }
}
