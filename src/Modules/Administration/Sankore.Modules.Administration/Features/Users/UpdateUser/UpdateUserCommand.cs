using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

public sealed record UpdateUserCommand(
    Guid UserId,
    string? FullName,
    Guid? AgencyId,
    List<string>? SpokenLanguages,
    List<string>? Specialties,
    bool? EnableNotifications,
    /// <summary>Null : inchangé. Chaîne vide : effacé.</summary>
    string? EmployeeNumber = null,
    string? FirstName = null,
    string? LastName = null,
    /// <summary>Accepté seulement avant activation ; renvoie alors l'invitation à la nouvelle adresse.</summary>
    string? Email = null,
    /// <summary>Null : inchangé. Chaîne vide : effacé.</summary>
    string? WorkPhone = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
