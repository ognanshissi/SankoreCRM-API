namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetKycFileHandler(KycDbContext db)
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
