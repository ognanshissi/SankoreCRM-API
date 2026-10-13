using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AgentSettings;

/// <param name="UserId">
/// Null : l'utilisateur connecté (son onglet Paramétrage agent). Renseigné : la fiche d'un
/// utilisateur vue par un administrateur, en lecture.
/// </param>
public sealed record GetMyAgentSettingsQuery(Guid? UserId = null) : IRequest<Result<AgentSettingsDto>>;

internal sealed class GetMyAgentSettingsHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMyAgentSettingsQuery, Result<AgentSettingsDto>>
{
    /// <summary>Semaine proposée à un agent qui n'a rien déclaré : du lundi au vendredi, 8 h – 17 h.</summary>
    private static readonly List<WorkingDayDto> DefaultWeek =
    [
        new(DayOfWeek.Monday, true, "08:00", "17:00"),
        new(DayOfWeek.Tuesday, true, "08:00", "17:00"),
        new(DayOfWeek.Wednesday, true, "08:00", "17:00"),
        new(DayOfWeek.Thursday, true, "08:00", "17:00"),
        new(DayOfWeek.Friday, true, "08:00", "17:00"),
        new(DayOfWeek.Saturday, false, "08:00", "12:00"),
        new(DayOfWeek.Sunday, false, "08:00", "12:00"),
    ];

    public async Task<Result<AgentSettingsDto>> Handle(GetMyAgentSettingsQuery request, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Agency)
            .FirstOrDefaultAsync(u => u.Id == (request.UserId ?? currentUser.Id), ct);
        if (user is null)
            return Result.Fail<AgentSettingsDto>("USER_NOT_FOUND");

        var settings = await db.AgentSettings.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);

        var nameIds = new List<Guid>();
        if (user.ReportsToUserId is { } managerId) nameIds.Add(managerId);
        if (settings?.ReplacementUserId is { } replacementId) nameIds.Add(replacementId);

        var now = DateTimeOffset.UtcNow;
        var attributions = await db.PermissionAttributions
            .Where(a => a.UserId == user.Id && a.IsActive && a.EndDate > now)
            .OrderBy(a => a.StartDate)
            .ToListAsync(ct);
        nameIds.AddRange(attributions.Select(a => a.AssignedByUserId));

        var names = await db.Users
            .Where(u => nameIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var codes = attributions.Select(a => a.PermissionCode).Distinct().ToList();
        var permissionLabels = await db.Permissions
            .Where(p => codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, p => p.Description, ct);

        var scopeIds = attributions.Where(a => a.ScopeId.HasValue).Select(a => a.ScopeId!.Value).ToList();
        var agencyNames = await db.Agencies.Where(a => scopeIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var territoryNames = await db.Territories.Where(t => scopeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var delegations = attributions.Select(a => new DelegationDto(
            a.Id,
            a.PermissionCode,
            permissionLabels.GetValueOrDefault(a.PermissionCode) is { Length: > 0 } label ? label : a.PermissionCode,
            a.ScopeType,
            a.ScopeId is { } scopeId
                ? agencyNames.GetValueOrDefault(scopeId) ?? territoryNames.GetValueOrDefault(scopeId)
                : null,
            names.GetValueOrDefault(a.AssignedByUserId),
            a.StartDate,
            a.EndDate)).ToList();

        // Chaque évènement figure une fois, réglé ou non : l'écran affiche une grille complète.
        var stored = settings?.NotificationPreferences ?? [];
        var preferences = AgentNotificationEvents.All
            .Select(e => stored.FirstOrDefault(p => p.Event == e) is { } p
                ? new NotificationPreferenceDto(e, p.Email, p.Sms)
                : new NotificationPreferenceDto(e, user.EnableNotifications, false))
            .ToList();

        var absence = settings?.CurrentAbsence is { } a
            ? new AbsenceDto(a.From, a.To, a.ReplacementUserId,
                a.ReplacementUserId is { } r ? names.GetValueOrDefault(r) : null, a.TransferTasks)
            : null;

        var configured = settings is { WorkingHours.Count: > 0 };

        return Result.Ok(new AgentSettingsDto(
            Availability: (settings?.Availability ?? AgentAvailability.Available).ToString(),
            Absence: absence,
            WorkingHoursConfigured: configured,
            WorkingHours: configured
                ? settings!.WorkingHours.Select(d => new WorkingDayDto(d.Day, d.Enabled, d.Start, d.End)).ToList()
                : DefaultWeek,
            MaxActiveLeads: settings?.MaxActiveLeads,
            CapacityCeiling: settings?.CapacityCeiling ?? Domain.AgentSettings.DefaultCapacityCeiling,
            ActiveLeadsCount: user.ActiveLeadsCount,
            NotificationPreferences: preferences,
            Products: user.Specialties,
            AgencyName: user.Agency?.Name,
            SpokenLanguages: user.SpokenLanguages,
            ManagerName: user.ReportsToUserId is { } m ? names.GetValueOrDefault(m) : null,
            Delegations: delegations,
            UpdatedAt: settings?.UpdatedAt));
    }
}

internal static class GetMyAgentSettingsEndpoint
{
    internal static IEndpointRouteBuilder MapGetMyAgentSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/agent-settings", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetMyAgentSettingsQuery(), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound);
            })
            .WithName("GetMyAgentSettings")
            .WithSummary("Paramétrage agent de l'utilisateur connecté : disponibilité, horaires, capacité, notifications, délégations.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces<AgentSettingsDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("{userId:guid}/agent-settings", async (Guid userId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetMyAgentSettingsQuery(userId), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound();
            })
            .WithName("GetUserAgentSettings")
            .WithSummary("Paramétrage agent d'un utilisateur, pour sa fiche d'administration : plafond, produits, et ce que l'agent a déclaré (disponibilité, horaires, langues). Requires permission: user:read.")
            .WithTags("Users")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<AgentSettingsDto>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
