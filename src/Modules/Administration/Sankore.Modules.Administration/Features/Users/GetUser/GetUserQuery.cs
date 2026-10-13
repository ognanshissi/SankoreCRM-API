using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUser;

public sealed record GetUserQuery(Guid UserId) : IRequest<Result<UserDto>>;

public sealed record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    Guid? AgencyId,
    string? AgencyName,
    bool MfaEnabled,
    DateTimeOffset PasswordExpiresAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? DeactivatedAt,
    List<string> SpokenLanguages,
    List<string> Specialties,
    bool IsAvailable,
    bool EnableNotifications,
    string AccountType,
    List<string> Roles,
    /// <summary>Who this user reports to, or null at the top of the line.</summary>
    Guid? ReportsToUserId,
    /// <summary>Resolved for display so the caller does not need a second round-trip.</summary>
    string? ReportsToFullName,
    string? EmployeeNumber = null,
    // ── Détail (GET users/{id} seulement ; la liste les laisse vides) ──
    string? FirstName = null,
    string? LastName = null,
    string? WorkPhone = null,
    string? AvatarDataUrl = null,
    DateTimeOffset? CreatedAt = null,
    string? CreatedByName = null,
    /// <summary>Dernier envoi du lien d'activation, pour un compte en attente.</summary>
    DateTimeOffset? InvitationSentAt = null,
    /// <summary>Expiration de ce lien ; passée, il faut renvoyer l'invitation.</summary>
    DateTimeOffset? InvitationExpiresAt = null,
    /// <summary>Libellés des rôles, dans l'ordre d'attribution.</summary>
    List<string>? RoleLabels = null,
    /// <summary>Permissions accordées à titre temporaire et encore en cours.</summary>
    int ActiveAttributionsCount = 0,
    /// <summary>Utilisateurs qui lui sont directement rattachés.</summary>
    int DirectReportsCount = 0,
    string? ReportsToRoleLabel = null,
    string? ReportsToAgencyName = null);
