namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetKycFileHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IKycSettings settings,
    IWorkflowModule workflow,
    KycVisibility? visibility = null)
    : IRequestHandler<GetKycFileQuery, Result<KycFileDto>>
{
    public async Task<Result<KycFileDto>> Handle(GetKycFileQuery request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant; a file of another tenant is
        // simply absent, and the caller is told NOT_FOUND rather than FORBIDDEN. Same rule as M01:
        // the existence of a record must not leak across tenants.
        var query = visibility?.Apply(db.KycFiles) ?? db.KycFiles.AsQueryable();

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
                f.WorkflowInstanceId,
                null,
                f.ProductLineId,
                f.ResponsibleAgentId,
                f.ValidationRoute == null ? null : f.ValidationRoute.ToString(),
                f.RoutingReason,
                f.RoutedAt))
            .FirstOrDefaultAsync(ct);

        if (dto is null)
            return Result.Fail<KycFileDto>(KycErrors.FileNotFound);

        // Only for a remotely collected file, and in a second query: the consent and the retake
        // counts live on the invitation, and joining them into the projection above would make
        // every counter file pay for a LEFT JOIN that is always null.
        var remote = await ReadRemoteFactsAsync(dto, ct);

        return Result.Ok(dto with
        {
            WorkflowStatus = await ReadWorkflowStatusAsync(dto, ct),
            ConsentGivenAt = remote.ConsentGivenAt,
            ConsentTextVersion = remote.ConsentTextVersion,
            ExcessiveQualityRetries = remote.ExcessiveQualityRetries,
        });
    }

    /// <summary>
    /// The two things KRM-F-08 shows that live on the invitation rather than on the file: when the
    /// customer consented (and to which version of the text), and which documents they had to
    /// photograph more often than the tenant allows.
    ///
    /// <para>
    /// Read from the NEWEST invitation of the file. A complement link supersedes the initial one,
    /// and the consent that matters to a validator looking at the evidence in front of them is the
    /// one given for the round that produced it.
    /// </para>
    ///
    /// <para>
    /// The retake budget is the tenant's <c>remote-max-quality-retries</c>, so the comparison
    /// happens here rather than in the front end — a screen that hard-coded five would stop
    /// agreeing with the server the day a compliance officer changed it, and would flag files
    /// nobody flagged.
    /// </para>
    /// </summary>
    private async Task<RemoteFacts> ReadRemoteFactsAsync(KycFileDto dto, CancellationToken ct)
    {
        if (dto.Channel != nameof(KycChannel.RemoteSelfService)) return new RemoteFacts();

        var invitation = await db.KycRemoteInvitations
            .Where(i => i.KycFileId == dto.Id)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (invitation is null) return new RemoteFacts();

        var budget = await settings.GetIntAsync(
            currentUser.TenantId, KycSettingKeys.RemoteMaxQualityRetries, ct);

        var excessive = budget <= 0
            ? []
            : invitation.QualityRetries()
                .Where(r => r.Value > budget)
                .Select(r => r.Key)
                .ToList();

        return new RemoteFacts
        {
            ConsentGivenAt = invitation.ConsentGivenAt,
            ConsentTextVersion = invitation.ConsentTextVersion,
            ExcessiveQualityRetries = excessive,
        };
    }

    /// <summary>
    /// The subset of <see cref="KycFileDto"/> this second read fills — a record rather than three
    /// out parameters, so adding a field here is a compile error at the one place that copies it.
    /// </summary>
    private readonly record struct RemoteFacts
    {
        public DateTimeOffset? ConsentGivenAt { get; init; }
        public string? ConsentTextVersion { get; init; }
        public IReadOnlyList<string>? ExcessiveQualityRetries { get; init; }
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
