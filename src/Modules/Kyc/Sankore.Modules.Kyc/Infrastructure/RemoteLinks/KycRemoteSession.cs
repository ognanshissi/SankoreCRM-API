namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// The three claims a remote-collection session carries, and the names they go by on the wire
/// (§4.3). Spelled once because the issuer writes them and the reader parses them, and a claim
/// whose two spellings drift produces a session that validates and then resolves to nothing.
/// </summary>
// Public for the scheme NAME alone, which the host registers. The rest is module vocabulary.
public static class KycRemoteSessionClaims
{
    public const string TenantId = "tenant_id";
    public const string KycFileId = "kyc_file_id";
    public const string InvitationId = "kyc_invitation_id";

    /// <summary>
    /// <c>scope=kyc:remote</c>. Checked by the authorization policy as well as the signing key and
    /// the audience — three independent reasons a token issued for anything else is refused here,
    /// and the only one that survives somebody reusing the key by mistake.
    /// </summary>
    public const string Scope = "scope";

    public const string RemoteScope = "kyc:remote";

    /// <summary>Name of the authentication scheme and of the policy that requires it.</summary>
    public const string SchemeName = "KycRemoteSession";
}

/// <summary>What a valid session resolves to.</summary>
internal sealed record KycRemoteSessionContext(Guid TenantId, Guid KycFileId, Guid InvitationId);

/// <summary>
/// Mints and reads the short-lived token that carries a customer through their own upload journey
/// (KRM-B-05).
/// </summary>
internal interface IKycRemoteSessions
{
    /// <summary>
    /// A signed session for one invitation, valid for <paramref name="minutes"/>.
    /// </summary>
    string Issue(Guid tenantId, Guid kycFileId, Guid invitationId, int minutes);

    /// <summary>
    /// The session behind the current request, or null when there is none — an unauthenticated
    /// call, or a token whose claims cannot be read.
    ///
    /// <para>
    /// Null and not an exception: every caller is a public endpoint that must answer the same
    /// 401 whatever went wrong, and distinguishing "no token" from "malformed token" would tell an
    /// anonymous caller which of the two they achieved.
    /// </para>
    /// </summary>
    KycRemoteSessionContext? Current();
}

/// <inheritdoc />
internal sealed class KycRemoteSessions(
    KycRemoteSessionOptions options,
    IHttpContextAccessor accessor)
    : IKycRemoteSessions
{
    public string Issue(Guid tenantId, Guid kycFileId, Guid invitationId, int minutes)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (kycFileId == Guid.Empty) throw new ArgumentException("KycFileId is required.", nameof(kycFileId));
        if (invitationId == Guid.Empty) throw new ArgumentException("InvitationId is required.", nameof(invitationId));
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(options.SigningKey), SecurityAlgorithms.HmacSha256);

        // NO subject, NO name, NO email, and no permission claim. A session is not a user: it
        // authorises one customer's upload journey into one file, and anything resembling an
        // identity here would be a value travelling in a token an anonymous caller holds.
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: KycRemoteSessionOptions.Audience,
            claims:
            [
                new Claim(KycRemoteSessionClaims.TenantId, tenantId.ToString()),
                new Claim(KycRemoteSessionClaims.KycFileId, kycFileId.ToString()),
                new Claim(KycRemoteSessionClaims.InvitationId, invitationId.ToString()),
                new Claim(KycRemoteSessionClaims.Scope, KycRemoteSessionClaims.RemoteScope),
            ],
            // No notBefore skew: the customer uses it immediately, and a clock difference that
            // would matter over 20 minutes is a clock problem worth seeing.
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public KycRemoteSessionContext? Current()
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        // The scope is re-checked here and not only in the policy. The policy is declarative and
        // one endpoint away from being forgotten; this read is on the path every handler takes.
        if (user.FindFirst(KycRemoteSessionClaims.Scope)?.Value != KycRemoteSessionClaims.RemoteScope)
            return null;

        if (!Guid.TryParse(user.FindFirst(KycRemoteSessionClaims.TenantId)?.Value, out var tenantId)
            || !Guid.TryParse(user.FindFirst(KycRemoteSessionClaims.KycFileId)?.Value, out var fileId)
            || !Guid.TryParse(user.FindFirst(KycRemoteSessionClaims.InvitationId)?.Value, out var invitationId))
        {
            return null;
        }

        if (tenantId == Guid.Empty || fileId == Guid.Empty || invitationId == Guid.Empty)
            return null;

        return new KycRemoteSessionContext(tenantId, fileId, invitationId);
    }
}
