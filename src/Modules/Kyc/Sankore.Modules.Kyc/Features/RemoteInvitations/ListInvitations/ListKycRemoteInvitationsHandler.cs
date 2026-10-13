namespace Sankore.Modules.Kyc.Features.RemoteInvitations.ListInvitations;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// The links issued for one KYC file, and whether another one can be (KRM-B-04, KRM-F-07).
/// </summary>
internal sealed class ListKycRemoteInvitationsHandler(
    KycDbContext db,
    KycRemoteEligibility eligibility,
    ICustomersModule customers,
    ICurrentUser currentUser,
    KycFileAccess fileAccess)
    : IRequestHandler<ListKycRemoteInvitationsQuery, Result<KycRemoteInvitationListDto>>
{
    public async Task<Result<KycRemoteInvitationListDto>> Handle(
        ListKycRemoteInvitationsQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        // The agency perimeter FIRST. It matters more here than on most reads, because the panel
        // this feeds offers a button that mails a bearer credential — and the existence check and
        // the perimeter check are one answer, KYC_FILE_NOT_FOUND, for the reason KycFileAccess
        // explains: a 403 would confirm another branch's file exists.
        if (!await fileAccess.CanActOnAsync(query.KycFileId, ct))
            return Result.Fail<KycRemoteInvitationListDto>(KycErrors.FileNotFound);

        var file = await db.KycFiles
            .Where(f => f.Id == query.KycFileId)
            .Select(f => new { f.CustomerId })
            .FirstOrDefaultAsync(ct);

        if (file is null)
            return Result.Fail<KycRemoteInvitationListDto>(KycErrors.FileNotFound);

        var rows = await db.KycRemoteInvitations
            .Where(i => i.KycFileId == query.KycFileId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        var verdict = await eligibility.EvaluateAsync(tenantId, query.KycFileId, ct);

        // Only when there IS something to confirm. An agent who cannot issue a link has no use for
        // the recipient, and this is the one call on the panel that decrypts.
        var maskedEmail = verdict.CanIssue
            ? await customers.GetMaskedEmailAsync(tenantId, file.CustomerId, ct)
            : null;

        return Result.Ok(new KycRemoteInvitationListDto(
            Invitations: [.. rows.Select(ToDto)],
            CanIssue: verdict.CanIssue,
            UnavailableReason: verdict.Reason,
            RecipientEmailMasked: maskedEmail,
            LinkValidityHours: verdict.Policy?.LinkValidityHours));
    }

    private static KycRemoteInvitationDto ToDto(KycRemoteInvitation i) => new(
        Id: i.Id,
        Status: i.Status.ToString(),
        Purpose: i.Purpose.ToString(),
        CreatedAt: i.CreatedAt,
        ExpiresAt: i.ExpiresAt,
        SentAt: i.SentAt,
        OpenedAt: i.OpenedAt,
        IdentifiedAt: i.IdentifiedAt,
        SubmittedAt: i.SubmittedAt,
        RevokedAt: i.RevokedAt,
        RevokeReason: i.RevokeReason,
        FailedAttempts: i.FailedAttempts,
        LockedAt: i.LockedAt,
        // Guid.Empty is the SYSTEM account — the conversion. Projected as a flag rather than as a
        // user id the screen would have to resolve for an answer that is almost always "nobody".
        IsSystemIssued: i.CreatedBy == Guid.Empty,
        IsActive: i.IsActive);
}
