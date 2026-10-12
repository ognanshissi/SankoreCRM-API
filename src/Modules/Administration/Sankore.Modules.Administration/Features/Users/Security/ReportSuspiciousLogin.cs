using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Sessions;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Security;

public sealed record ReportSuspiciousLoginRequest(Guid? LoginId, string? Comment);

/// <summary>
/// « Une connexion ne vient pas de vous ? » La réponse doit agir tout de suite, sans attendre
/// personne : les autres sessions sont fermées, la ligne signalée est marquée, et le superviseur
/// est prévenu pour la suite. Passe par l'audit (ICommand) : c'est une trace d'incident.
/// </summary>
public sealed record ReportSuspiciousLoginCommand(Guid? CurrentSessionId, Guid? LoginId, string? Comment)
    : IRequest<Result<int>>, ICommand;

internal sealed class ReportSuspiciousLoginValidator : AbstractValidator<ReportSuspiciousLoginCommand>
{
    public ReportSuspiciousLoginValidator() => RuleFor(x => x.Comment).MaximumLength(1000);
}

internal sealed class ReportSuspiciousLoginHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    SuspiciousLoginNotifier notifier)
    : IRequestHandler<ReportSuspiciousLoginCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ReportSuspiciousLoginCommand command, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Result.Fail<int>("USER_NOT_FOUND");

        UserLoginLocation? login = null;
        if (command.LoginId is { } loginId)
        {
            login = await db.UserLoginLocations.AsTracking()
                .FirstOrDefaultAsync(l => l.Id == loginId && l.UserId == user.Id, ct);
            if (login is null)
                return Result.Fail<int>("Connexion introuvable dans votre historique.");
            login.MarkReported();
        }

        // Sans session courante identifiable, tout est fermé — appelant compris. Mieux vaut se
        // reconnecter que laisser ouverte la session qu'on vient de signaler.
        var ended = await SessionTerminator.EndAsync(
            db, user.Id, SessionEndReason.SuspiciousLoginReported,
            keepSessionId: command.CurrentSessionId, ct: ct);

        await db.SaveChangesAsync(ct);
        await notifier.NotifyAsync(user, login, command.Comment, ended, ct);
        return Result.Ok(ended);
    }
}
