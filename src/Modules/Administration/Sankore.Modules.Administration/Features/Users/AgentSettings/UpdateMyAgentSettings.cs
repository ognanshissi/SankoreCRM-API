using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AgentSettings;

public sealed record UpdateMyAgentSettingsRequest(
    string Availability,
    AbsenceRequest? Absence,
    List<WorkingDayDto>? WorkingHours,
    int? MaxActiveLeads,
    List<NotificationPreferenceDto>? NotificationPreferences,
    List<string>? SpokenLanguages,
    bool? NotificationSound = null);

public sealed record AbsenceRequest(DateOnly From, DateOnly To, Guid? ReplacementUserId, bool TransferTasks);

/// <summary>
/// L'agent règle son propre paramétrage. Pas de paramètre d'utilisateur : la cible est toujours
/// l'appelant, ce qui dispense d'une permission et interdit d'écrire celui d'un collègue.
/// </summary>
public sealed record UpdateMyAgentSettingsCommand(UpdateMyAgentSettingsRequest Request)
    : IRequest<Result>, ICommand;

internal sealed class UpdateMyAgentSettingsValidator : AbstractValidator<UpdateMyAgentSettingsCommand>
{
    public UpdateMyAgentSettingsValidator()
    {
        RuleFor(x => x.Request.Availability)
            .Must(v => Enum.TryParse<AgentAvailability>(v, ignoreCase: true, out _))
            .WithMessage("Statut inconnu : Available, Paused ou Absent.");
        RuleFor(x => x.Request.Absence)
            .NotNull()
            .When(x => string.Equals(x.Request.Availability, nameof(AgentAvailability.Absent), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Indiquez les dates de l'absence.");
        RuleFor(x => x.Request.MaxActiveLeads).GreaterThan(0).When(x => x.Request.MaxActiveLeads.HasValue);
        RuleFor(x => x.Request.SpokenLanguages!.Count).LessThanOrEqualTo(20).When(x => x.Request.SpokenLanguages is not null);
    }
}

internal sealed class UpdateMyAgentSettingsHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    AgentAbsenceNotifier notifier)
    : IRequestHandler<UpdateMyAgentSettingsCommand, Result>
{
    public async Task<Result> Handle(UpdateMyAgentSettingsCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Result.Fail("USER_NOT_FOUND");

        var availability = Enum.Parse<AgentAvailability>(req.Availability, ignoreCase: true);
        AbsenceDeclaration? absence = req.Absence is { } a
            ? new AbsenceDeclaration(a.From, a.To, a.ReplacementUserId, a.TransferTasks)
            : null;

        // Le remplaçant doit être un collègue actif du même tenant : un identifiant inconnu
        // transférerait des tâches à personne.
        AppUser? replacement = null;
        if (availability == AgentAvailability.Absent && absence?.ReplacementUserId is { } replacementId)
        {
            replacement = await db.Users.FirstOrDefaultAsync(
                u => u.Id == replacementId && u.Status == UserStatus.Active, ct);
            if (replacement is null)
                return Result.Fail("Le remplaçant choisi n'est pas un utilisateur actif.");
        }

        var settings = await db.AgentSettings.AsTracking().FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        var isNew = settings is null;
        settings ??= Domain.AgentSettings.Create(user.TenantId, user.Id);

        bool absenceChanged;
        try
        {
            absenceChanged = settings.UpdateBySelf(
                availability,
                absence,
                (req.WorkingHours ?? []).Select(d => new WorkingDay(d.Day, d.Enabled, d.Start, d.End)).ToList(),
                req.MaxActiveLeads,
                (req.NotificationPreferences ?? []).Select(p => new NotificationPreference(p.Event, p.Email, p.Sms)).ToList());

            if (req.SpokenLanguages is not null)
                user.UpdateDetails(null, null, req.SpokenLanguages, null, null);

            // Null leaves the bell as it was: a screen that does not show the switch must not
            // turn the sound back on by omission.
            if (req.NotificationSound is { } sound)
                settings.SetNotificationSound(sound);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        if (isNew) db.AgentSettings.Add(settings);

        if (absenceChanged && settings.CurrentAbsence is { } declared)
        {
            // Dans la transaction de l'enregistrement, par l'outbox : le transfert des tâches part
            // si et seulement si l'absence est bien enregistrée.
            await publisher.PublishAsync(new AgentAbsenceDeclaredEvent(
                user.TenantId, user.Id, declared.From, declared.To,
                declared.ReplacementUserId, declared.TransferTasks), ct);
        }

        await db.SaveChangesAsync(ct);

        if (absenceChanged && settings.CurrentAbsence is { } notified)
            await notifier.NotifyManagerAsync(user, notified, replacement, ct);

        return Result.Ok();
    }
}

internal static class UpdateMyAgentSettingsEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateMyAgentSettings(this IEndpointRouteBuilder app)
    {
        app.MapPut("me/agent-settings", async (UpdateMyAgentSettingsRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new UpdateMyAgentSettingsCommand(req), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("UpdateMyAgentSettings")
            .WithSummary("Enregistre le paramétrage agent de l'utilisateur connecté. Une absence nouvelle prévient le superviseur et peut transférer les tâches en cours au remplaçant.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
