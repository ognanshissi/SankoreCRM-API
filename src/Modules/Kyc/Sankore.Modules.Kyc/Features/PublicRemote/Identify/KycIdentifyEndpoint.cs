namespace Sankore.Modules.Kyc.Features.PublicRemote.Identify;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// The customer proves who they are and receives a session (KRM-B-05).
///
/// <para>
/// <b>Two factors on two channels.</b> The link arrives in one email and the client number in
/// another; the date of birth is known only to the customer. One intercepted message is therefore
/// not enough — which is the entire reason the invitation email deliberately omits the client
/// number.
/// </para>
///
/// <para>
/// <b>One message for every failure.</b> Wrong number, wrong date, both wrong, a client with no
/// recorded date of birth: all answer the same code and the same shape. Telling them apart would
/// turn this endpoint into an oracle — "that client number exists" is exactly the half an attacker
/// cannot get anywhere else, and from there a date of birth is a few thousand guesses.
/// </para>
///
/// <para>
/// <b>The guessing bound is the link's own counter, not the rate limiter.</b> The IP limiter in
/// front of this endpoint bounds a flood and resets on every deployment; the counter on the
/// invitation is durable, is per-token, and locks the link for good at the tenant's threshold.
/// A thin endpoint rather than a MediatR slice for one reason that outweighs the convention here:
/// a command is serialised into the audit trail by <c>AuditBehavior</c>, and this one's payload is
/// a client number and a date of birth.
/// </para>
/// </summary>
internal static class KycIdentifyEndpoint
{
    internal static IEndpointRouteBuilder MapKycIdentify(this IEndpointRouteBuilder app)
    {
        app.MapPost("link/identify", Handle)
            .WithName("IdentifyKycRemoteCustomer")
            .WithSummary("Exchange a link, a client number and a date of birth for a session")
            .WithDescription(
                "Anonymous, rate-limited. Every failure answers 401 with the single code "
                + "KYC_REMOTE_IDENTIFICATION_FAILED and no indication of which field was wrong. "
                + "The link locks for good after the tenant's remote-max-identify-attempts, and a "
                + "successful identification resets the counter. The session is a JWT with its own "
                + "key and audience, accepted nowhere but under /api/v1/public/kyc/session.")
            .AllowAnonymous()
            .Produces<KycIdentifyResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status423Locked)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// The ONE failure the customer is ever told. Not a list, on purpose.
    /// </summary>
    internal const string FailedCode = "KYC_REMOTE_IDENTIFICATION_FAILED";

    /// <summary>Returned once the link is locked, so the page can show the dead end rather than retrying.</summary>
    internal const string LockedCode = "KYC_REMOTE_LINK_LOCKED";

    internal const string LockedEmailTemplate = "kyc.remote-link-locked";

    private static async Task<IResult> Handle(
        KycIdentifyRequest request,
        KycRemoteGate gate,
        KycDbContext db,
        ICustomersModule customers,
        IAdministrationModule administration,
        IKycRemoteSessions sessions,
        IKycSettings settings,
        IKycOtpSender otpSender,
        IAuditWriter audit,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(KycIdentifyEndpoint));

        // track: false — the status probe already recorded the open. Counting this as a second
        // visit would make OpenedAt mean "last touched".
        var resolution = await gate.ResolveLinkAsync(request?.Token, track: false, ct);

        if (resolution.State == KycRemoteGate.LinkState.WrongTenant)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (resolution.State == KycRemoteGate.LinkState.Locked)
            return Results.Json(new { error = LockedCode }, statusCode: StatusCodes.Status423Locked);

        // Unknown, expired, consumed, revoked, or a file that has moved on: the same 401 as a
        // wrong answer. The page has already asked the status endpoint and knows which it is; this
        // one must not become a second way to probe a token.
        if (!resolution.IsUsable)
            return Unauthorized();

        var invitation = resolution.Invitation!;

        var verified = await customers.VerifyRemoteIdentityAsync(
            invitation.TenantId, invitation.CustomerId,
            request!.ClientNumber, request.BirthDate, ct);

        if (!verified)
        {
            var maxAttempts = await settings.GetIntAsync(
                invitation.TenantId, KycSettingKeys.RemoteMaxIdentifyAttempts, ct);

            var locked = invitation.RecordFailedIdentification(maxAttempts, clock);
            await db.SaveChangesAsync(ct);

            // The attempt is audited WITHOUT the values that were typed — the whole point of the
            // boolean contract with M01 is that neither this endpoint nor its audit row ever holds
            // a client number or a date of birth.
            await WriteAuditAsync(
                audit, invitation,
                locked ? "KycRemoteIdentificationLocked" : "KycRemoteIdentificationFailed",
                ct);

            if (locked)
            {
                logger.LogWarning(
                    "Remote collection link {InvitationId} locked after {Attempts} failed "
                    + "identification attempts (tenant {TenantId})",
                    invitation.Id, invitation.FailedAttempts, invitation.TenantId);

                await NotifyAgentOfLockAsync(db, administration, invitation, ct);

                return Results.Json(
                    new { error = LockedCode }, statusCode: StatusCodes.Status423Locked);
            }

            // Deliberately NOT telling the customer how many tries remain. It would tell an
            // attacker how much budget they have left, and it would tell the real customer a
            // number that frightens them out of trying again.
            return Unauthorized();
        }

        var identified = invitation.MarkIdentified(clock);
        if (identified.IsFailure)
            return Unauthorized();

        var minutes = await settings.GetIntAsync(
            invitation.TenantId, KycSettingKeys.RemoteSessionMinutes, ct);

        // KRM-B-12: with the second factor required, the two answers the customer just gave are
        // NOT enough — a code goes to their telephone and the session waits for it. The invitation
        // is still marked Identified: they proved what they could prove, and the agent's panel
        // should show that the customer got that far rather than reporting a link nobody opened.
        var otpRequired = await settings.GetBoolAsync(
            invitation.TenantId, KycSettingKeys.RemoteOtp, ct);

        if (otpRequired)
        {
            var sent = await IssueOtpAsync(invitation, otpSender, clock, logger, ct);

            await db.SaveChangesAsync(ct);
            await WriteAuditAsync(audit, invitation, "KycRemoteOtpSent", ct);

            // A code that could not be sent is NOT a session. Falling back to opening one would
            // turn the tenant's second factor off at the exact moment the channel failed, which is
            // the one behaviour a second factor must not have — so the customer is told to try
            // again and the link stays usable.
            return sent
                ? Results.Ok(KycIdentifyResponse.AwaitingCode())
                : Results.Json(
                    new { error = KycErrors.RemoteOtpUnavailable },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(audit, invitation, "KycRemoteIdentified", ct);

        var token = sessions.Issue(
            invitation.TenantId, invitation.KycFileId, invitation.Id, minutes);

        logger.LogInformation(
            "Remote collection session opened for KYC file {KycFileId} (link {InvitationId})",
            invitation.KycFileId, invitation.Id);

        return Results.Ok(new KycIdentifyResponse(token, minutes * 60));
    }

    /// <summary>
    /// Mints a code, stores its digest on the invitation and hands the clear value to the sender
    /// (KRM-B-12).
    ///
    /// <para>
    /// The clear code exists inside this method and nowhere else — not in the row, not in the log
    /// line, not in the audit payload, not in the response. The same arrangement
    /// <c>SendKycRemoteInvitationJob</c> makes for the link token, and for the same reason: a
    /// credential that is written down once is a credential in a backup.
    /// </para>
    ///
    /// <para>
    /// <b>The digest is stored BEFORE the message is sent.</b> The two cannot be atomic — the
    /// sender is another system — so the order is chosen for which failure is survivable. Storing
    /// first can leave a code the customer never received, and they ask for another; sending first
    /// could deliver a code no row would accept, which is a customer typing the right digits into
    /// a screen that refuses them.
    /// </para>
    /// </summary>
    internal static async Task<bool> IssueOtpAsync(
        KycRemoteInvitation invitation,
        IKycOtpSender otpSender,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        var code = KycRemoteOtpCodes.Mint();
        var hash = KycRemoteOtpCodes.HashOrNull(invitation.Id, code);

        // Mint always produces six digits, so a null here is a bug in one of the two and not a
        // state to handle gracefully — reported rather than swallowed into a silent "no code".
        if (hash is null)
        {
            logger.LogError(
                "A minted OTP was rejected by its own shape check (link {InvitationId})",
                invitation.Id);
            return false;
        }

        var issued = invitation.IssueOtp(hash, KycRemoteOtpCodes.Validity, clock);
        if (issued.IsFailure) return false;

        var sent = await otpSender.SendAsync(
            invitation.TenantId, invitation.CustomerId, code, ct);

        if (!sent)
        {
            logger.LogWarning(
                "The second factor for link {InvitationId} could not be delivered (tenant "
                + "{TenantId}). The customer is asked to try again; no session was opened.",
                invitation.Id, invitation.TenantId);
        }

        return sent;
    }

    /// <summary>
    /// 401 with the single code, and nothing else in the body. Shaped once so the three call sites
    /// cannot drift into three slightly different answers — which is how an oracle appears.
    /// </summary>
    private static IResult Unauthorized()
        => Results.Json(new { error = FailedCode }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Records the attempt against the FILE, with no value from the form.
    /// </summary>
    /// <remarks>
    /// Best-effort: an audit sink that is down must not hand an anonymous caller a 500, and must
    /// not undo a lock that has already been committed. The lock is the control; the audit row is
    /// the trail.
    /// </remarks>
    private static async Task WriteAuditAsync(
        IAuditWriter audit, KycRemoteInvitation invitation, string action, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditEntry(
                Timestamp: DateTimeOffset.UtcNow,
                // The SYSTEM actor: there is no user behind an anonymous request, and inventing
                // one would put a customer's action under an employee's name.
                UserId: Guid.Empty,
                TenantId: invitation.TenantId,
                Action: action,
                // EMPTY payload, and that is the point. There is exactly one thing worth recording
                // here — that an attempt happened on this link — and the two values that were
                // typed are the two that must never be written down. AuditBehavior serialises a
                // command's whole payload, which is why this endpoint is not a command.
                PayloadJson: "{}",
                Outcome: "Success",
                ErrorDetail: null,
                ResourceType: "KycRemoteInvitation",
                ResourceId: invitation.Id.ToString()),
                ct);
        }
        catch (Exception)
        {
            // Swallowed: see the remarks.
        }
    }

    /// <summary>
    /// Tells the agent responsible for the file that their customer is locked out, so they can
    /// re-issue a link (KRM-B-05).
    /// </summary>
    private static async Task NotifyAgentOfLockAsync(
        KycDbContext db,
        IAdministrationModule administration,
        KycRemoteInvitation invitation,
        CancellationToken ct)
    {
        var agentId = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == invitation.TenantId && f.Id == invitation.KycFileId)
            .Select(f => f.ResponsibleAgentId)
            .FirstOrDefaultAsync(ct);

        if (agentId is not { } id || id == Guid.Empty) return;

        // NotifyUserAsync never throws and resolves the agent's address, name and locale itself.
        // The payload names no customer: a rendered body sits in a mail provider's delivery log,
        // and the agent opens the file to see who it is.
        await administration.NotifyUserAsync(new NotifyUserRequest(
            TenantId: invitation.TenantId,
            UserId: id,
            TemplateKey: LockedEmailTemplate,
            // Keyed on the invitation: one lock per link produces one mail, however many further
            // attempts arrive against the dead link.
            IdempotencyKey: $"{LockedEmailTemplate}-{invitation.Id}",
            TemplateData: new Dictionary<string, object>
            {
                ["kyc_file_id"] = invitation.KycFileId,
                ["locked_at"] = invitation.LockedAt ?? DateTimeOffset.UtcNow,
            }), ct);
    }
}

/// <param name="ClientNumber">As printed in the welcome email. Spacing and casing are forgiven.</param>
/// <param name="BirthDate">The customer's own date of birth.</param>
public sealed record KycIdentifyRequest(string? Token, string? ClientNumber, DateOnly? BirthDate);

/// <param name="SessionToken">
/// Bearer token for <c>/api/v1/public/kyc/session/*</c> and nowhere else — its own signing key and
/// its own audience, so it is refused by every other endpoint of the platform.
/// </param>
/// <param name="ExpiresInSeconds">
/// So the page can warn before it lapses. A lapsed session sends the customer back to the
/// identification screen and KEEPS the photos they already sent.
/// </param>
public sealed record KycIdentifyResponse(
    string SessionToken,
    int ExpiresInSeconds,
    /// <summary>
    /// True when a code has been sent and the session waits for it (KRM-B-12). The two fields
    /// above are then EMPTY — not a placeholder session, because a page that received a token
    /// would skip the code screen and a page that received a zero lifetime would show a session
    /// about to lapse.
    /// </summary>
    bool OtpRequired = false)
{
    /// <summary>The second factor is pending: no session yet, and the page asks for the code.</summary>
    internal static KycIdentifyResponse AwaitingCode()
        => new(string.Empty, 0, OtpRequired: true);
}
