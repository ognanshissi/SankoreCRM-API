namespace Sankore.Modules.Kyc.Features.PublicRemote.Otp;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.PublicRemote.Identify;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// The second factor of KRM-B-12: verifying the code, and asking for another one.
///
/// <para>
/// <b>Thin endpoints and not MediatR slices</b>, for the reason the identification endpoint next
/// door is not one: <c>AuditBehavior</c> serialises a command's whole payload into the audit trail,
/// and these payloads are a bearer token and a one-time code. The attempts are still audited —
/// with an empty payload, which is the entire point.
/// </para>
///
/// <para>
/// <b>Why the resend endpoint exists at all.</b> A text message that does not arrive is the
/// ordinary failure of this mechanism, and without a resend the customer's only recourse is to
/// burn one of their five identification attempts to get back to this screen. It carries its own
/// bound so it cannot become a way to make the platform send somebody a message every second —
/// on a per-message channel that bound is also the budget.
/// </para>
/// </summary>
internal static class KycOtpEndpoints
{
    /// <summary>
    /// How much of the window must have elapsed before another code is minted. Two thirds: it
    /// lets a customer who waited and saw nothing ask again well inside the ten minutes, and it
    /// stops a page with a retry loop from sending ten messages.
    /// </summary>
    private static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(3);

    internal static IEndpointRouteBuilder MapKycOtpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("link/otp", Verify)
            .WithName("VerifyKycRemoteOtp")
            .WithSummary("Exchange a link and an SMS code for a session")
            .WithDescription(
                "Anonymous, rate-limited. Only reachable on a tenant that requires the second "
                + "factor (remote-otp) and only after a successful identification. Every failure "
                + "answers 401 with the single code KYC_REMOTE_OTP_INVALID — wrong, stale and "
                + "out of tries are not told apart, because that would say whether a code is "
                + "currently live. Three wrong tries exhaust the code; asking for a new one "
                + "resets that count and never resets the per-link identification counter.")
            .AllowAnonymous()
            .Produces<KycIdentifyResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status423Locked)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi();

        app.MapPost("link/otp/resend", Resend)
            .WithName("ResendKycRemoteOtp")
            .WithSummary("Send the SMS code again")
            .WithDescription(
                "Anonymous, rate-limited. Refused with KYC_REMOTE_OTP_TOO_SOON while the "
                + "previous code is recent, so a retry loop cannot bill a tenant for a message a "
                + "second. A new code resets the three-try count on the code and never resets the "
                + "per-link identification counter.")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Verify(
        KycOtpRequest? request,
        KycRemoteGate gate,
        KycDbContext db,
        IKycRemoteSessions sessions,
        IKycSettings settings,
        IAuditWriter audit,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(KycOtpEndpoints));

        // track: false — the status probe already recorded the open, and this is the third call of
        // the same visit. Counting it would make OpenedAt mean "last touched".
        var resolution = await gate.ResolveLinkAsync(request?.Token, track: false, ct);

        if (resolution.State == KycRemoteGate.LinkState.WrongTenant)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (resolution.State == KycRemoteGate.LinkState.Locked)
        {
            return Results.Json(
                new { error = KycIdentifyEndpoint.LockedCode },
                statusCode: StatusCodes.Status423Locked);
        }

        if (!resolution.IsUsable) return Invalid();

        var invitation = resolution.Invitation!;

        var verdict = invitation.VerifyOtp(
            KycRemoteOtpCodes.HashOrNull(invitation.Id, request!.Code),
            KycRemoteOtpCodes.MaxAttempts,
            clock);

        // Saved whatever the verdict: a wrong try increments a counter that must survive the
        // response, and an accepted code has been consumed and must not be accepted twice.
        await db.SaveChangesAsync(ct);

        if (verdict != KycOtpVerdict.Accepted)
        {
            await WriteAuditAsync(audit, invitation, "KycRemoteOtpFailed", ct);

            // The verdict is logged — an operator diagnosing "nobody can get through" needs to
            // know whether codes are stale or wrong — and the CUSTOMER is told one thing.
            logger.LogInformation(
                "Second factor refused on link {InvitationId}: {Verdict}",
                invitation.Id, verdict);

            return Invalid();
        }

        var minutes = await settings.GetIntAsync(
            invitation.TenantId, KycSettingKeys.RemoteSessionMinutes, ct);

        await WriteAuditAsync(audit, invitation, "KycRemoteOtpAccepted", ct);

        var token = sessions.Issue(
            invitation.TenantId, invitation.KycFileId, invitation.Id, minutes);

        logger.LogInformation(
            "Remote collection session opened for KYC file {KycFileId} after a second factor "
            + "(link {InvitationId})",
            invitation.KycFileId, invitation.Id);

        return Results.Ok(new KycIdentifyResponse(token, minutes * 60));
    }

    private static async Task<IResult> Resend(
        KycOtpResendRequest? request,
        KycRemoteGate gate,
        KycDbContext db,
        IKycSettings settings,
        IKycOtpSender otpSender,
        IAuditWriter audit,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(KycOtpEndpoints));

        var resolution = await gate.ResolveLinkAsync(request?.Token, track: false, ct);

        if (resolution.State == KycRemoteGate.LinkState.WrongTenant)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!resolution.IsUsable) return Invalid();

        var invitation = resolution.Invitation!;

        // Only on a tenant that asks for the factor, and only once the FIRST one has been passed.
        // Without the second condition this endpoint would send a text message to any customer
        // whose link an attacker holds, with no identification at all — a nuisance channel pointed
        // at a real telephone number.
        var required = await settings.GetBoolAsync(
            invitation.TenantId, KycSettingKeys.RemoteOtp, ct);

        if (!required || invitation.IdentifiedAt is null) return Invalid();

        var now = clock.GetUtcNow();

        // A live code that is still recent: refused, so a retry loop cannot bill the tenant for a
        // message a second. Expressed on the code's own issue time rather than on a separate
        // column — the window is derived from ExpiresAt, which is the only timestamp it has.
        if (invitation.HasPendingOtpAt(now)
            && invitation.OtpExpiresAt is { } expiry
            && expiry - KycRemoteOtpCodes.Validity + ResendAfter > now)
        {
            return Results.Json(
                new { error = KycErrors.RemoteOtpTooSoon },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var sent = await KycIdentifyEndpoint.IssueOtpAsync(
            invitation, otpSender, clock, logger, ct);

        await db.SaveChangesAsync(ct);
        await WriteAuditAsync(audit, invitation, "KycRemoteOtpResent", ct);

        return sent
            ? Results.NoContent()
            : Results.Json(
                new { error = KycErrors.RemoteOtpUnavailable },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The ONE failure a customer is told about the second factor. Wrong digits, a code whose
    /// window has closed, a code that has already been used and three tries spent all answer
    /// this: telling them apart would say whether a live code exists for a link somebody holds.
    /// </summary>
    private static IResult Invalid()
        => Results.Json(
            new { error = KycErrors.RemoteOtpInvalid },
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Records that something happened on this link, with an EMPTY payload — the code and the
    /// token are the two things that must never be written down. Best-effort, like its neighbour:
    /// an audit sink that is down must not hand an anonymous caller a 500, and must not undo an
    /// attempt counter that is already committed.
    /// </summary>
    private static async Task WriteAuditAsync(
        IAuditWriter audit, KycRemoteInvitation invitation, string action, CancellationToken ct)
    {
        try
        {
            await audit.WriteAsync(new AuditEntry(
                Timestamp: DateTimeOffset.UtcNow,
                UserId: Guid.Empty,
                TenantId: invitation.TenantId,
                Action: action,
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
}

/// <param name="Code">The six digits the customer received. Spacing is forgiven.</param>
public sealed record KycOtpRequest(string? Token, string? Code);

public sealed record KycOtpResendRequest(string? Token);
