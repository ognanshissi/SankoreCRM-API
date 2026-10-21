namespace Sankore.Modules.Leads.Features.GetMyAgenda;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

public sealed record GetMyAgendaQuery(DateTimeOffset? From, int Days) : IRequest<Result<MyAgendaDto>>;

/// <summary>
/// Ce qui attend l'appelant en dehors de ses tâches : ses rappels, ses rendez-vous et activités
/// planifiées, et les alertes SLA de ses leads. L'écran « Ma journée » n'avait que les tâches, et
/// rappels comme rendez-vous ne se lisaient que lead par lead.
///
/// <para>
/// « À moi » veut dire : j'en suis l'auteur, ou je détiens le lead (<c>CurrentAssignedId</c>, à défaut
/// <c>OwnerId</c>). Un rappel posé par l'appel téléphonique d'un collègue sur un lead qui m'a été
/// réaffecté depuis reste donc dans mon agenda.
/// </para>
///
/// <para>
/// Une activité « à venir » est une activité dont <c>ScheduledAt</c> tombe dans la fenêtre : le
/// modèle n'a pas de statut « planifiée », et c'est la « Date/heure prévue » que l'écran d'activité
/// propose pour un rendez-vous ou une visite.
/// </para>
/// </summary>
internal sealed class GetMyAgendaHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    ILeadSettings settings,
    TimeProvider clock)
    : IRequestHandler<GetMyAgendaQuery, Result<MyAgendaDto>>
{
    public const int MaxDays = 62;

    private static readonly LeadStatus[] ClosedStatuses =
        [LeadStatus.Converted, LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    public async Task<Result<MyAgendaDto>> Handle(GetMyAgendaQuery query, CancellationToken ct)
    {
        if (query.Days is < 1 or > MaxDays)
            return Result.Fail<MyAgendaDto>($"La fenêtre doit compter entre 1 et {MaxDays} jours.");

        var now = clock.GetUtcNow();
        // `from` vient du client : c'est le début de SA journée, que le serveur, en UTC, ne connaît pas.
        var from = query.From ?? now;
        var to = from.AddDays(query.Days);
        var me = currentUser.Id;

        // ── Rappels en attente : en retard compris, sans borne basse ─────────────────────
        var reminders = await db.LeadReminders.AsNoTracking()
            .Where(r => r.Status == ReminderStatus.Pending && r.DueAt < to)
            .Join(db.Leads, r => r.LeadId, l => l.Id, (r, l) => new { r, l })
            .Where(x => x.r.CreatedBy == me
                     || x.l.CurrentAssignedId == me
                     || (x.l.CurrentAssignedId == null && x.l.OwnerId == me))
            .OrderBy(x => x.r.DueAt)
            .Select(x => new AgendaReminderDto(
                x.r.Id, x.r.LeadId, x.l.FullName, x.r.Title, x.r.Notes, x.r.DueAt, x.r.DueAt < now))
            .ToListAsync(ct);

        // ── Activités planifiées dans la fenêtre ─────────────────────────────────────
        var activities = await db.LeadActivities.AsNoTracking()
            .Where(a => a.ScheduledAt != null && a.ScheduledAt >= from && a.ScheduledAt < to)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new { a, l })
            .Where(x => x.a.PerformedBy == me
                     || x.l.CurrentAssignedId == me
                     || (x.l.CurrentAssignedId == null && x.l.OwnerId == me))
            .OrderBy(x => x.a.ScheduledAt)
            .Select(x => new AgendaActivityDto(
                x.a.Id, x.a.LeadId, x.l.FullName, x.a.Type, x.a.Subject, x.a.Notes,
                x.a.ScheduledAt!.Value, x.a.DurationMinutes))
            .ToListAsync(ct);

        // ── Alertes SLA de premier contact, sur mes affectations en cours ─────────────
        // Le seuil d'alerte est une méthode du domaine, pas une expression SQL : on charge mes
        // affectations ouvertes (quelques dizaines au plus) et on trie en mémoire.
        var open = await db.LeadAssignments.AsNoTracking()
            .Where(a => a.AgentId == me && a.SupersededAt == null && a.FirstContactAt == null)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new { a, l.FullName, l.Status })
            .Where(x => !ClosedStatuses.Contains(x.Status))
            .ToListAsync(ct);

        var percent = await settings.GetIntAsync(currentUser.TenantId, LeadSettingKeys.SlaWarningThresholdPercent, ct);

        var alerts = open
            .Select(x => x.a.SlaDeadline < now
                ? new AgendaAlertDto(AgendaAlertKind.SlaBreached, x.a.LeadId, x.FullName, x.a.SlaDeadline)
                : x.a.SlaWarningAt(percent) is { } warnAt && now >= warnAt
                    ? new AgendaAlertDto(AgendaAlertKind.SlaAtRisk, x.a.LeadId, x.FullName, x.a.SlaDeadline)
                    : null)
            .OfType<AgendaAlertDto>()
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.Deadline)
            .ToList();

        return Result.Ok(new MyAgendaDto(from, to, reminders, activities, alerts));
    }
}

public sealed record MyAgendaDto(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<AgendaReminderDto> Reminders,
    IReadOnlyList<AgendaActivityDto> Activities,
    IReadOnlyList<AgendaAlertDto> Alerts);

public sealed record AgendaReminderDto(
    Guid Id,
    Guid LeadId,
    string LeadName,
    string Title,
    string? Notes,
    DateTimeOffset DueAt,
    bool IsOverdue);

public sealed record AgendaActivityDto(
    Guid Id,
    Guid LeadId,
    string LeadName,
    ActivityType Type,
    string Subject,
    string? Notes,
    DateTimeOffset ScheduledAt,
    int? DurationMinutes);

/// <summary>L'ordre des valeurs est celui d'affichage : le dépassé avant le menacé.</summary>
public enum AgendaAlertKind
{
    SlaBreached,
    SlaAtRisk,
}

public sealed record AgendaAlertDto(
    AgendaAlertKind Kind,
    Guid LeadId,
    string LeadName,
    DateTimeOffset Deadline);

internal static class GetMyAgendaEndpoint
{
    internal static IEndpointRouteBuilder MapGetMyAgenda(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/agenda", async (ISender sender, CancellationToken ct, DateTimeOffset? from = null, int days = 14) =>
            {
                var result = await sender.Send(new GetMyAgendaQuery(from, days), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetMyAgenda")
            .WithSummary("Rappels, activités planifiées et alertes SLA de l'utilisateur connecté, sur une fenêtre de jours.")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<MyAgendaDto>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
