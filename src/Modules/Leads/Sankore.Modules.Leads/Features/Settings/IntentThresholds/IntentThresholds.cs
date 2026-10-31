namespace Sankore.Modules.Leads.Features.Settings.IntentThresholds;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The temperature a score reads as, per institution (lead temperature, lot 3): Hot from
/// <see cref="Hot"/>, Warm from <see cref="Warm"/>, Cold below. Every scored lead has one —
/// « Inconnu » is left to a lead nobody ever scored (before lot 2, or set by hand). Before, the
/// thresholds were 80/60/40 in two copies, and a lead under 40 read « Inconnu ».
/// </summary>
public sealed record IntentThresholds(int Warm, int Hot)
{
    public static readonly IntentThresholds Default = new(60, 80);

    public LeadIntentLevel Of(int score) =>
        score >= Hot ? LeadIntentLevel.Hot
        : score >= Warm ? LeadIntentLevel.Warm
        : LeadIntentLevel.Cold;

    /// <summary>1 ≤ warm &lt; hot ≤ 100: a warm threshold of 0 would make every lead warm, and the
    /// two equal would leave no warm band at all.</summary>
    public bool IsValid => Warm >= 1 && Hot <= 100 && Warm < Hot;

    /// <summary>
    /// The tenant's thresholds, through the settings' 60-second cache. A pair set out of order
    /// through the generic settings route (one key at a time) reads as the defaults rather than
    /// as a band that classes everything hot.
    /// </summary>
    internal static async Task<IntentThresholds> ReadAsync(LeadsDbContext db, Guid tenantId, CancellationToken ct)
    {
        var settings = new LeadSettingsService(db);
        var read = new IntentThresholds(
            await settings.GetIntAsync(tenantId, LeadSettingKeys.IntentWarmThreshold, ct),
            await settings.GetIntAsync(tenantId, LeadSettingKeys.IntentHotThreshold, ct));
        return read.IsValid ? read : Default;
    }
}

/// <summary>
/// Both thresholds at once — so the order is checked on the pair — and the temperature of the
/// open leads already scored is read again from their stored score, so the lists and the dispatch
/// agree with the new bands at once rather than at each lead's next rescore.
/// </summary>
internal sealed record SetIntentThresholdsCommand(Guid TenantId, int Warm, int Hot, Guid Actor, bool? CooldownEnabled = null)
    : IRequest<Result<int>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSettings";
    public string? ResourceId => "intent-thresholds";
}

internal sealed class SetIntentThresholdsHandler(LeadsDbContext db, ILeadSettings settings)
    : IRequestHandler<SetIntentThresholdsCommand, Result<int>>
{
    public const string OutOfOrder = "INTENT_THRESHOLDS_INVALID";

    private static readonly LeadStatus[] Closed =
        [LeadStatus.Converted, LeadStatus.Archived, LeadStatus.Lost, LeadStatus.Disqualified];

    public async Task<Result<int>> Handle(SetIntentThresholdsCommand cmd, CancellationToken ct)
    {
        var thresholds = new IntentThresholds(cmd.Warm, cmd.Hot);
        if (!thresholds.IsValid) return Result.Fail<int>(OutOfOrder);

        foreach (var (key, value) in new[] { (LeadSettingKeys.IntentWarmThreshold, cmd.Warm), (LeadSettingKeys.IntentHotThreshold, cmd.Hot) })
        {
            var set = await settings.SetAsync(cmd.TenantId, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), cmd.Actor, ct);
            if (set.IsFailure) return Result.Fail<int>(set.Error!);
        }
        // Absent: unchanged. It acts at the next rescore (the nightly one at the latest).
        if (cmd.CooldownEnabled is { } cooldown)
        {
            var set = await settings.SetAsync(cmd.TenantId, LeadSettingKeys.LeadCooldownEnabled, cooldown ? "true" : "false", cmd.Actor, ct);
            if (set.IsFailure) return Result.Fail<int>(set.Error!);
        }

        // Scored leads only (score > 0): a lead nobody scored keeps « Inconnu ». Closed leads keep
        // the temperature they closed with.
        var open = db.Leads.Where(l => l.TenantId == cmd.TenantId && l.Score > 0 && !l.IsAnonymized && !Closed.Contains(l.Status));
        if (db.Database.IsRelational())
            return Result.Ok(await open.ExecuteUpdateAsync(s => s.SetProperty(l => l.IntentLevel, l =>
                l.Score >= cmd.Hot ? LeadIntentLevel.Hot : l.Score >= cmd.Warm ? LeadIntentLevel.Warm : LeadIntentLevel.Cold), ct));

        // EF InMemory has no ExecuteUpdate.
        var leads = await open.AsTracking().ToListAsync(ct);
        foreach (var lead in leads) lead.UpdateIntentLevel(thresholds.Of(lead.Score));
        await db.SaveChangesAsync(ct);
        return Result.Ok(leads.Count);
    }
}

/// <param name="CooldownEnabled">Whether a lead without activity loses points (−10 at 30 days, −20 at 60, −30 at 90).</param>
public sealed record IntentThresholdsDto(int Warm, int Hot, bool CooldownEnabled = true);

public sealed record SetIntentThresholdsRequest(int Warm, int Hot, bool? CooldownEnabled = null);

public sealed record SetIntentThresholdsResultDto(int Warm, int Hot, int LeadsUpdated, bool CooldownEnabled = true);

internal static class IntentThresholdsEndpoints
{
    internal static IEndpointRouteBuilder MapIntentThresholds(this IEndpointRouteBuilder group)
    {
        group.MapGet("intent-thresholds", async (LeadsDbContext db, ILeadSettings settings, ITenantContext tenant, CancellationToken ct) =>
            {
                var t = await IntentThresholds.ReadAsync(db, tenant.CurrentTenantId, ct);
                var cooldown = await settings.GetBoolAsync(tenant.CurrentTenantId, LeadSettingKeys.LeadCooldownEnabled, ct);
                return Results.Ok(new IntentThresholdsDto(t.Warm, t.Hot, cooldown));
            })
            .WithName("GetIntentThresholds")
            .WithSummary("Scores from which a lead reads warm and hot")
            .WithDescription("Hot from `hot`, warm from `warm`, cold below. Any signed-in user: the lists and « Ma journée » read them.")
            .RequireAuthorization()
            .Produces<IntentThresholdsDto>()
            .WithTenantHeader();

        group.MapPut("intent-thresholds", async (SetIntentThresholdsRequest req, ISender sender, ILeadSettings settings, ITenantContext tenant, ICurrentUser me, CancellationToken ct) =>
            {
                var result = await sender.Send(new SetIntentThresholdsCommand(tenant.CurrentTenantId, req.Warm, req.Hot, me.Id, req.CooldownEnabled), ct);
                return result.IsSuccess
                    ? Results.Ok(new SetIntentThresholdsResultDto(req.Warm, req.Hot, result.Value,
                        await settings.GetBoolAsync(tenant.CurrentTenantId, LeadSettingKeys.LeadCooldownEnabled, ct)))
                    : Results.Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("SetIntentThresholds")
            .WithSummary("Change the warm and hot thresholds")
            .WithDescription(
                "Both at once, 1 ≤ warm < hot ≤ 100 (else 400 INTENT_THRESHOLDS_INVALID). The open leads already "
                + "scored take the temperature of their stored score at once; the answer says how many. "
                + "cooldownEnabled (optional) switches the inactivity penalty; it acts at the next rescore. "
                + "Requires permission: lead:dispatching-rule:manage — the bands drive the dispatch's SLA.")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces<SetIntentThresholdsResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .WithTenantHeader();
        return group;
    }
}
