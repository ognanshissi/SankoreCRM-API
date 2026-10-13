using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Une connexion d'un appareil, de la saisie du mot de passe à la déconnexion. Les jetons de
/// rafraîchissement tournent à chaque usage ; la session est ce qui reste stable sous eux, et
/// c'est elle que l'utilisateur voit et coupe depuis « Sessions actives ».
///
/// Son identifiant voyage dans le jeton d'accès (claim <c>sid</c>) : une session révoquée refuse
/// aussi les jetons d'accès déjà émis, sans attendre leurs 8 heures de validité.
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Dernière activité constatée — rafraîchissement ou requête, à quelques minutes près.</summary>
    public DateTimeOffset LastSeenAt { get; private set; }

    public string? IpAddress { get; private set; }
    public string? Browser { get; private set; }
    public LoginPlatform Platform { get; private set; } = LoginPlatform.Unknown;
    public LoginClientKind ClientKind { get; private set; } = LoginClientKind.Unknown;

    /// <summary>
    /// Renseigné pour une session d'usurpation : l'administrateur qui agit sous l'identité de
    /// <see cref="UserId"/>. Une telle session n'a pas de jeton de rafraîchissement et meurt avec
    /// son jeton d'accès.
    /// </summary>
    public Guid? ImpersonatorUserId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }
    public SessionEndReason? EndReason { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    private UserSession() { }

    public static UserSession Open(Guid tenantId, Guid userId, string? ipAddress, UserAgentInfo? userAgent)
    {
        var now = DateTimeOffset.UtcNow;
        return new UserSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            CreatedAt = now,
            LastSeenAt = now,
            IpAddress = Trim(ipAddress, UserLoginLocation.MaxIpLength),
            Browser = Trim(userAgent?.Browser, UserLoginLocation.MaxBrowserLength),
            Platform = userAgent?.Platform ?? LoginPlatform.Unknown,
            ClientKind = userAgent?.ClientKind ?? LoginClientKind.Unknown,
        };
    }

    public static UserSession OpenImpersonation(
        Guid tenantId, Guid userId, Guid impersonatorUserId, string? ipAddress, UserAgentInfo? userAgent)
    {
        var session = Open(tenantId, userId, ipAddress, userAgent);
        session.ImpersonatorUserId = impersonatorUserId;
        return session;
    }

    public void Touch(DateTimeOffset now)
    {
        if (now > LastSeenAt) LastSeenAt = now;
    }

    /// <summary>Idempotent : la première raison reste, une session ne se termine qu'une fois.</summary>
    public void End(SessionEndReason reason)
    {
        if (IsRevoked) return;
        RevokedAt = DateTimeOffset.UtcNow;
        EndReason = reason;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public enum SessionEndReason
{
    /// <summary>L'utilisateur s'est déconnecté depuis cet appareil.</summary>
    Logout,

    /// <summary>Coupée à distance, depuis une autre session.</summary>
    RevokedByUser,

    /// <summary>Coupée parce que l'utilisateur a signalé une connexion qui n'était pas la sienne.</summary>
    SuspiciousLoginReported,

    /// <summary>Le mot de passe a changé : les autres appareils doivent se reconnecter.</summary>
    PasswordChanged,

    /// <summary>L'administrateur est revenu à son propre compte.</summary>
    ImpersonationEnded,
}
