using MediatR;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Sessions;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Security;

/// <summary>
/// Ferme une session précise (<paramref name="TargetSessionId"/>) ou toutes les autres (null).
/// Toujours celles de l'appelant : la cible est filtrée par son identifiant d'utilisateur.
/// </summary>
public sealed record EndMySessionsCommand(Guid? CurrentSessionId, Guid? TargetSessionId)
    : IRequest<Result<int>>, ICommand;

internal sealed class EndMySessionsHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<EndMySessionsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(EndMySessionsCommand command, CancellationToken ct)
    {
        if (command.TargetSessionId is { } target && target == command.CurrentSessionId)
            return Result.Fail<int>("Pour fermer cette session, utilisez « Se déconnecter ».");

        // Sans session courante connue (jeton d'avant les sessions), « les autres » ne se
        // distinguent pas de celle-ci : on refuse plutôt que de déconnecter l'appelant à son insu.
        if (command.TargetSessionId is null && command.CurrentSessionId is null)
            return Result.Fail<int>("Reconnectez-vous pour pouvoir fermer vos autres sessions.");

        var ended = await SessionTerminator.EndAsync(
            db, currentUser.Id, SessionEndReason.RevokedByUser,
            onlySessionId: command.TargetSessionId,
            keepSessionId: command.TargetSessionId is null ? command.CurrentSessionId : null,
            ct: ct);

        if (command.TargetSessionId is not null && ended == 0)
            return Result.Fail<int>("Cette session est déjà fermée.");

        await db.SaveChangesAsync(ct);
        return Result.Ok(ended);
    }
}
