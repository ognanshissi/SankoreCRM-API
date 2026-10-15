namespace Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Shared.Kernel;

internal sealed class IssueKycRemoteInvitationHandler(
    KycDbContext db,
    KycRemoteEligibility eligibility,
    IKycSettings settings,
    IBackgroundJobClient hangfire,
    TimeProvider clock,
    ILogger<IssueKycRemoteInvitationHandler> logger)
    : IRequestHandler<IssueKycRemoteInvitationCommand, Result<IssueKycRemoteInvitationResult>>
{
    public async Task<Result<IssueKycRemoteInvitationResult>> Handle(
        IssueKycRemoteInvitationCommand cmd, CancellationToken ct)
    {
        var verdict = await eligibility.EvaluateAsync(cmd.TenantId, cmd.KycFileId, ct);

        // Le refus du canal CHOISI, et non « l'un des deux marche » : un agent qui demande un SMS
        // pour un client sans téléphone doit lire « pas de téléphone », et non voir partir un
        // e-mail qu'il n'a pas demandé.
        if (verdict.File is null || verdict.ReasonFor(cmd.Channel) is not null)
        {
            return Result.Fail<IssueKycRemoteInvitationResult>(
                verdict.ReasonFor(cmd.Channel) ?? KycErrors.RemoteInvitationNotFound);
        }

        var file = verdict.File!;
        var policy = verdict.Policy!;
        var now = clock.GetUtcNow();

        // A List and not the static array: on .NET 10 an array's Contains binds to the
        // ReadOnlySpan<T> extension and no longer translates to SQL. A List does.
        var activeStatuses = KycRemoteInvitation.ActiveStatuses.ToList();

        var active = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(i => i.TenantId == cmd.TenantId && i.KycFileId == cmd.KycFileId)
            .Where(i => activeStatuses.Contains(i.Status))
            .FirstOrDefaultAsync(ct);

        if (active is not null)
        {
            // Still usable and nobody asked to replace it: the caller's intent is satisfied.
            // This is the redelivered-conversion-event path, and the alternative — mailing a
            // second link — would both spam the customer and invalidate the one they are
            // probably reading at that moment.
            if (!cmd.ReplaceActive && active.IsUsableAt(now))
            {
                logger.LogDebug(
                    "KYC file {KycFileId} already holds a usable collection link {InvitationId}",
                    cmd.KycFileId, active.Id);

                return Result.Ok(new IssueKycRemoteInvitationResult(
                    active.Id, active.ExpiresAt, Reused: true));
            }

            // Replaced, or expired-but-not-yet-swept. Revoked rather than left alongside, because
            // the filtered unique index allows exactly one active row per file — and because two
            // live links into one file means an intercepted older one still works.
            var revoked = active.Revoke(
                cmd.ReplaceActive ? ReplacedReason : WindowClosedReason,
                cmd.IssuedBy == Guid.Empty ? null : cmd.IssuedBy,
                clock);

            if (revoked.IsFailure)
                return Result.Fail<IssueKycRemoteInvitationResult>(revoked.Error!);
        }

        var invitation = KycRemoteInvitation.Issue(
            tenantId: cmd.TenantId,
            kycFileId: file.Id,
            customerId: file.CustomerId,
            validityHours: policy.LinkValidityHours,
            createdBy: cmd.IssuedBy,
            clock: clock,
            purpose: cmd.Purpose,
            requestedKinds: cmd.RequestedKinds,
            channel: cmd.Channel);

        db.KycRemoteInvitations.Add(invitation);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveLinkUniqueViolation(ex))
        {
            // Lost a race: another agent clicked at the same moment, or a redelivered conversion
            // event overlapped one. The read above covers the ordinary case and this index covers
            // the simultaneous one — the same two layers CreateKycFileHandler uses, and for the
            // same reason: a read can be overtaken between its query and its commit.
            //
            // The caller's intent is "this customer holds a link", and the winner's link satisfies
            // it. Reported as a reuse rather than an error, so an agent never sees a 500 over a
            // double click.
            logger.LogInformation(
                "Concurrent collection link issue for KYC file {KycFileId}; keeping the winner",
                cmd.KycFileId);

            var winner = await db.KycRemoteInvitations
                .IgnoreQueryFilters()
                .Where(i => i.TenantId == cmd.TenantId
                         && i.KycFileId == cmd.KycFileId
                         && activeStatuses.Contains(i.Status))
                .Select(i => new { i.Id, i.ExpiresAt })
                .FirstOrDefaultAsync(ct);

            return winner is not null
                ? Result.Ok(new IssueKycRemoteInvitationResult(
                    winner.Id, winner.ExpiresAt, Reused: true))
                // The violation was real but the row is gone: something closed it between the two
                // statements. Report the code rather than invent an id.
                : Result.Fail<IssueKycRemoteInvitationResult>(KycErrors.RemoteInvitationNotFound);
        }

        // AFTER the commit, deliberately. A job enqueued inside the transaction can be picked up
        // by a Hangfire worker before that transaction commits — Hangfire has its own connection —
        // and the job would then find no invitation and give up. The cost of this ordering is a
        // link that stays Scheduled for ever if the process dies in this exact gap, which the
        // nightly expiry sweep turns into a visible Expired row for the agent to act on.
        var delay = TimeSpan.FromMinutes(
            cmd.DelayMinutes
            ?? await settings.GetIntAsync(cmd.TenantId, KycSettingKeys.RemoteEmailDelayMinutes, ct));

        var invitationId = invitation.Id;
        var tenantId = cmd.TenantId;

        // The payload is the invitation id and the tenant, and nothing else — no token (it does
        // not exist yet, by design), no email address, no customer name. A Hangfire argument list
        // is a row in a shared database and a line on a dashboard.
        if (delay <= TimeSpan.Zero)
            hangfire.Enqueue<SendKycRemoteInvitationJob>(job => job.ExecuteAsync(invitationId, tenantId));
        else
            hangfire.Schedule<SendKycRemoteInvitationJob>(
                job => job.ExecuteAsync(invitationId, tenantId), delay);

        logger.LogInformation(
            "Collection link {InvitationId} issued for KYC file {KycFileId}; "
            + "email queued in {DelayMinutes} min, link valid until {ExpiresAt}",
            invitation.Id, file.Id, delay.TotalMinutes, invitation.ExpiresAt);

        return Result.Ok(new IssueKycRemoteInvitationResult(
            invitation.Id, invitation.ExpiresAt, Reused: false));
    }

    /// <summary>
    /// Motives for the two automatic revocations this handler performs. Fixed strings rather than
    /// an operator's words, and in French because an agent reads them next to the ones agents
    /// typed themselves.
    /// </summary>
    private const string ReplacedReason = "Remplacé par un nouveau lien";

    private const string WindowClosedReason = "Durée de validité écoulée";

    /// <summary>
    /// Narrow on purpose: only the one-active-link index may be swallowed. Any other constraint
    /// violation is a bug and must keep propagating instead of being reported as a reused link.
    /// </summary>
    private static bool IsActiveLinkUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains(
               "ux_kyc_remote_invitations_active_per_file", StringComparison.OrdinalIgnoreCase)
           == true;
}
