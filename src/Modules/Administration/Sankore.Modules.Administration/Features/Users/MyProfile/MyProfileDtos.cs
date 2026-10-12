namespace Sankore.Modules.Administration.Features.Users.MyProfile;

/// <summary>L'onglet Informations de « Mon compte » : ce que l'utilisateur règle, et ce qu'il lit seulement.</summary>
public sealed record MyProfileDto(
    string FirstName,
    string LastName,
    string Email,
    /// <summary>Attribué par l'administrateur, en lecture seule ici.</summary>
    string? EmployeeNumber,
    string? WorkPhone,
    string? WhatsAppNumber,
    string? Signature,
    string InterfaceLanguage,
    string TimeZone,
    string AmountFormat,
    /// <summary>Data URL prête pour un <c>&lt;img&gt;</c>, ou null sans photo.</summary>
    string? AvatarDataUrl,
    string? RoleLabel,
    string? AgencyName,
    string? ManagerName,
    /// <summary>Null pour un compte antérieur dont l'arrivée n'a pas pu être reconstituée.</summary>
    DateTimeOffset? MemberSince,
    DateTimeOffset? UpdatedAt);

public sealed record UpdateMyProfileRequest(
    string FirstName,
    string LastName,
    string? WorkPhone,
    string? WhatsAppNumber,
    string? Signature,
    string InterfaceLanguage,
    string TimeZone,
    string AmountFormat);
