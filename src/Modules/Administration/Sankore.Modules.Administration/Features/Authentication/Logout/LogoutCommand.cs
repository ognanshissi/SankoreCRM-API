using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.Logout;

/// <param name="SessionId">
/// La session de l'appareil qui se déconnecte. Fournie, seule elle est fermée : se déconnecter du
/// téléphone ne doit pas couper l'ordinateur. Absente (jeton d'avant les sessions), tout est fermé.
/// </param>
public sealed record LogoutCommand(Guid UserId, Guid? SessionId = null) : IRequest<Result>, ICommand;