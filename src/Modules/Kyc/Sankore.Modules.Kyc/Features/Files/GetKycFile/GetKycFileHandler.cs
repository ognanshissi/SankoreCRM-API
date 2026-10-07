namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetKycFileHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IWorkflowModule workflow)
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
                f.UpdatedAt,
                f.WorkflowInstanceId))
            .FirstOrDefaultAsync(ct);

        if (dto is null)
            return Result.Fail<KycFileDto>(KycErrors.FileNotFound);

        return Result.Ok(dto with { WorkflowStatus = await ReadWorkflowStatusAsync(dto, ct) });
    }

    /// <summary>
    /// The state of the file's mirrored workflow instance, or null when there is none to read.
    ///
    /// <para>
    /// Carried on this read rather than left to M12's own HTTP surface for two reasons: it costs one
    /// lookup by primary key instead of a second round trip from the screen, and reading it through
    /// M12's instance endpoint would demand <c>workflow:instance:view</c> from a KYC validator who
    /// has no other business there.
    /// </para>
    ///
    /// <para>
    /// It exists because the mirror can disagree with this file and must do so VISIBLY. A rung that
    /// blows its SLA leaves the instance <c>TimedOut</c> while the file sits in <c>Validating</c>,
    /// governed by M02 as always — an operator needs to see that the deadline was missed, not
    /// discover it in a log. Best-effort: a failure here must not break a file read, so it comes
    /// back null and the screen simply shows nothing.
    /// </para>
    /// </summary>
    private async Task<string?> ReadWorkflowStatusAsync(KycFileDto dto, CancellationToken ct)
    {
        if (dto.WorkflowInstanceId is not { } instanceId) return null;

        try
        {
            var status = await workflow.GetInstanceStatusAsync(instanceId, currentUser.TenantId, ct);
            return status.IsSuccess ? status.Value.Status : null;
        }
        catch
        {
            return null;
        }
    }
}
