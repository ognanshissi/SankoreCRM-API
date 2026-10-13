namespace Sankore.Modules.Kyc.Features.RemoteInvitations.ListInvitations;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListKycRemoteInvitationsQuery(Guid KycFileId)
    : IRequest<Result<KycRemoteInvitationListDto>>;

/// <summary>
/// What the agent's "KYC à distance" panel needs (KRM-F-07): the links issued for a file, and —
/// when none can be — the reason, which is the half of the screen that stops a support call.
/// </summary>
/// <param name="Invitations">Newest first. Spent links stay: their dates are the history.</param>
/// <param name="CanIssue">
/// Whether a link may be issued right now. Computed, not inferred from
/// <paramref name="UnavailableReason"/> being null, because a caller reading the two separately is
/// how a button ends up enabled next to an explanation of why it cannot work.
/// </param>
/// <param name="UnavailableReason">
/// Null when a link may be issued. Otherwise a <c>KYC_REMOTE_INVITATION_*</c> code the front
/// translates — "no email", "product not eligible", "file no longer collecting".
/// </param>
/// <param name="RecipientEmailMasked">
/// The address a new link would go to, masked (<c>j***@e***.com</c>), or null when there is none.
/// KRM-F-07 asks the confirmation dialog to show the recipient, and an agent confirming a send
/// needs to recognise the address without the screen handing them a customer's contact detail in
/// clear — that stays behind M01's audited reveal endpoint.
/// </param>
/// <param name="LinkValidityHours">
/// How long a new link would last, from the product's policy. Null when no policy applies. Shown
/// so the agent can tell the customer, and so the number on the screen is the one the server will
/// actually use rather than a hard-coded 72.
/// </param>
public sealed record KycRemoteInvitationListDto(
    IReadOnlyList<KycRemoteInvitationDto> Invitations,
    bool CanIssue,
    string? UnavailableReason,
    string? RecipientEmailMasked,
    int? LinkValidityHours);

/// <summary>
/// One link, as an agent sees it.
///
/// <para>
/// Carries no token and no hash. There is deliberately no way to learn what the link WAS: the
/// screen's job is to say how far the customer got, and an agent who needs them to start again
/// issues a new one.
/// </para>
/// </summary>
/// <param name="Status">
/// <c>Scheduled</c> | <c>Sent</c> | <c>Opened</c> | <c>Identified</c> | <c>Submitted</c> |
/// <c>Expired</c> | <c>Revoked</c> | <c>Locked</c>.
/// </param>
/// <param name="Purpose"><c>Initial</c> | <c>Complement</c>.</param>
/// <param name="IsSystemIssued">
/// True when the conversion issued it, false when an agent did. <c>CreatedBy</c> itself is not
/// exposed as a user id the screen would then have to resolve for an answer that is almost always
/// "the system".
/// </param>
public sealed record KycRemoteInvitationDto(
    Guid Id,
    string Status,
    string Purpose,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? IdentifiedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason,
    int FailedAttempts,
    DateTimeOffset? LockedAt,
    bool IsSystemIssued,
    bool IsActive);
