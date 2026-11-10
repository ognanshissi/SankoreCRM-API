namespace Sankore.Modules.Leads.Features.Settings.KeyAccounts;

using System.Globalization;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// What makes a company prospect a big one, per institution (key accounts lot 2, 2026-11-09, product
/// owner): two yearly-purchase thresholds and the targeted sectors. They feed the score's « Potentiel »
/// dimension (<see cref="QualifyLead.LeadScoreCalculator"/>), which is worth nothing until one of
/// them is set — no existing score moves without an administrator's decision.
/// </summary>
/// <param name="GoodPurchases">From this yearly amount a company is a « bon client »; 0 = not set.</param>
/// <param name="MajorPurchases">From this one a « grand compte »; 0 = not set.</param>
public sealed record KeyAccountSettings(decimal GoodPurchases, decimal MajorPurchases, IReadOnlyList<string> TargetSectors)
{
    public static readonly KeyAccountSettings None = new(0, 0, []);

    public bool IsConfigured => GoodPurchases > 0 || MajorPurchases > 0 || TargetSectors.Count > 0;

    /// <summary>Points of the « Potentiel » dimension, by criterion.</summary>
    public const int MajorPoints = 12;
    public const int GoodPoints = 6;
    public const int TargetSectorPoints = 4;

    public static int SizePoints(LeadCompanySize? size) => size switch
    {
        LeadCompanySize.Large  => 4,
        LeadCompanySize.Medium => 3,
        LeadCompanySize.Small  => 1,
        _                      => 0,
    };

    /// <summary>
    /// 0 to 20 for a company prospect, 0 for a person or while nothing is set. The purchases are
    /// compared in the institution's single currency: a change of currency converts nothing, as
    /// everywhere else.
    /// </summary>
    public int PotentialOf(Lead lead)
    {
        if (!IsConfigured || lead.ProspectType != LeadType.Corporate) return 0;

        var purchases = lead.EstimatedAnnualPurchases?.Amount ?? 0;
        var points = MajorPurchases > 0 && purchases >= MajorPurchases ? MajorPoints
                   : GoodPurchases > 0 && purchases >= GoodPurchases ? GoodPoints
                   : 0;
        points += SizePoints(lead.CompanySize);
        if (lead.ActivitySectorCode is { } sector && TargetSectors.Contains(sector)) points += TargetSectorPoints;
        return points;
    }

    /// <summary>Where a lead stands against the thresholds; a person is always <see cref="LeadPotentialTier.None"/>.</summary>
    public LeadPotentialTier TierOf(Lead lead) => TierOf(lead.ProspectType, lead.EstimatedAnnualPurchases?.Amount);

    public LeadPotentialTier TierOf(LeadType type, decimal? yearlyPurchases)
    {
        if (type != LeadType.Corporate) return LeadPotentialTier.None;
        var purchases = yearlyPurchases ?? 0;
        if (MajorPurchases > 0 && purchases >= MajorPurchases) return LeadPotentialTier.KeyAccount;
        if (GoodPurchases > 0 && purchases >= GoodPurchases) return LeadPotentialTier.GoodCustomer;
        return LeadPotentialTier.None;
    }

    /// <summary>
    /// The same reading as <see cref="TierOf"/>, as a query predicate — the list, its counts, its
    /// export and the reports filter on it. A threshold not set matches nobody.
    /// </summary>
    public IQueryable<Lead> WhereTier(IQueryable<Lead> q, LeadPotentialTier tier)
    {
        var major = MajorPurchases;
        var good = GoodPurchases;
        return tier switch
        {
            LeadPotentialTier.KeyAccount when major > 0 => q.Where(l => l.ProspectType == LeadType.Corporate
                && l.EstimatedAnnualPurchases != null && l.EstimatedAnnualPurchases.Amount >= major),
            LeadPotentialTier.GoodCustomer when good > 0 => q.Where(l => l.ProspectType == LeadType.Corporate
                && l.EstimatedAnnualPurchases != null && l.EstimatedAnnualPurchases.Amount >= good
                && (major <= 0 || l.EstimatedAnnualPurchases.Amount < major)),
            LeadPotentialTier.None => q.Where(l => l.ProspectType != LeadType.Corporate
                || l.EstimatedAnnualPurchases == null
                || ((good <= 0 || l.EstimatedAnnualPurchases.Amount < good) && (major <= 0 || l.EstimatedAnnualPurchases.Amount < major))),
            _ => q.Where(_ => false),
        };
    }

    /// <returns>Null when valid, else the error code.</returns>
    public string? Problem()
    {
        if (GoodPurchases < 0 || MajorPurchases < 0) return KeyAccountSettingsHandler.Invalid;
        if (GoodPurchases > 0 && MajorPurchases > 0 && GoodPurchases >= MajorPurchases) return KeyAccountSettingsHandler.Invalid;
        if (TargetSectors.Any(s => LeadActivitySectors.Normalize(s) is null)) return "LEAD_ACTIVITY_SECTOR_INVALID";
        return null;
    }

    internal static async Task<KeyAccountSettings> ReadAsync(LeadsDbContext db, Guid tenantId, CancellationToken ct)
    {
        var settings = new LeadSettingsService(db);
        var sectors = (await settings.GetStringAsync(tenantId, LeadSettingKeys.KeyAccountTargetSectors, ct))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(LeadActivitySectors.Normalize)
            .OfType<string>()
            .Distinct()
            .ToList();
        var read = new KeyAccountSettings(
            await settings.GetDecimalAsync(tenantId, LeadSettingKeys.KeyAccountGoodPurchases, ct),
            await settings.GetDecimalAsync(tenantId, LeadSettingKeys.KeyAccountMajorPurchases, ct),
            sectors);
        // A pair written out of order key by key through the generic route reads as « not set »
        // rather than as a scale that ranks a good customer above a key account.
        return read.Problem() is null ? read : read with { GoodPurchases = 0, MajorPurchases = 0 };
    }
}

/// <summary>Sets the three together, then rescores the open company prospects in the background.</summary>
internal sealed record SetKeyAccountSettingsCommand(
    Guid TenantId, decimal GoodPurchases, decimal MajorPurchases, IReadOnlyList<string> TargetSectors, Guid Actor)
    : IRequest<Result<KeyAccountSettings>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSettings";
    public string? ResourceId => "key-accounts";
}

internal sealed class KeyAccountSettingsHandler(ILeadSettings settings, IBackgroundJobClient? hangfire = null)
    : IRequestHandler<SetKeyAccountSettingsCommand, Result<KeyAccountSettings>>
{
    public const string Invalid = "KEY_ACCOUNT_THRESHOLDS_INVALID";

    public async Task<Result<KeyAccountSettings>> Handle(SetKeyAccountSettingsCommand cmd, CancellationToken ct)
    {
        var next = new KeyAccountSettings(
            cmd.GoodPurchases, cmd.MajorPurchases,
            cmd.TargetSectors.Select(s => LeadActivitySectors.Normalize(s) ?? s).Distinct().ToList());
        if (next.Problem() is { } problem) return Result.Fail<KeyAccountSettings>(problem);

        foreach (var (key, value) in new[]
                 {
                     (LeadSettingKeys.KeyAccountGoodPurchases, next.GoodPurchases.ToString(CultureInfo.InvariantCulture)),
                     (LeadSettingKeys.KeyAccountMajorPurchases, next.MajorPurchases.ToString(CultureInfo.InvariantCulture)),
                     (LeadSettingKeys.KeyAccountTargetSectors, string.Join(',', next.TargetSectors)),
                 })
        {
            var set = await settings.SetAsync(cmd.TenantId, key, value, cmd.Actor, ct);
            if (set.IsFailure) return Result.Fail<KeyAccountSettings>(set.Error!);
        }

        // After the commit: the job reads the settings the command just wrote.
        hangfire?.Enqueue<RescoreCompanyLeadsJob>(j => j.ExecuteAsync(cmd.TenantId));
        return Result.Ok(next);
    }
}

public sealed record KeyAccountSettingsDto(decimal GoodPurchases, decimal MajorPurchases, IReadOnlyList<string> TargetSectors, bool IsConfigured);

public sealed record SetKeyAccountSettingsRequest(decimal GoodPurchases, decimal MajorPurchases, IReadOnlyList<string>? TargetSectors = null);

internal static class KeyAccountSettingsEndpoints
{
    internal static IEndpointRouteBuilder MapKeyAccountSettings(this IEndpointRouteBuilder group)
    {
        group.MapGet("key-accounts", async (LeadsDbContext db, ITenantContext tenant, CancellationToken ct) =>
            {
                var s = await KeyAccountSettings.ReadAsync(db, tenant.CurrentTenantId, ct);
                return Results.Ok(new KeyAccountSettingsDto(s.GoodPurchases, s.MajorPurchases, s.TargetSectors, s.IsConfigured));
            })
            .WithName("GetKeyAccountSettings")
            .WithSummary("What makes a company prospect a big one")
            .WithDescription("Yearly-purchase thresholds (0 = not set) and targeted sectors, read by the score's « Potentiel » dimension.")
            .RequireAuthorization()
            .Produces<KeyAccountSettingsDto>()
            .WithTenantHeader();

        group.MapPut("key-accounts", async (SetKeyAccountSettingsRequest req, ISender sender, ITenantContext tenant, ICurrentUser me, CancellationToken ct) =>
            {
                var result = await sender.Send(new SetKeyAccountSettingsCommand(
                    tenant.CurrentTenantId, req.GoodPurchases, req.MajorPurchases, req.TargetSectors ?? [], me.Id), ct);
                return result.IsSuccess
                    ? Results.Ok(new KeyAccountSettingsDto(result.Value.GoodPurchases, result.Value.MajorPurchases,
                        result.Value.TargetSectors, result.Value.IsConfigured))
                    : Results.Problem(detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("SetKeyAccountSettings")
            .WithSummary("Change what makes a company prospect a big one")
            .WithDescription(
                "Both thresholds and the sectors at once. 0 ≤ good < major when both are set (else 400 "
                + "KEY_ACCOUNT_THRESHOLDS_INVALID); an unknown sector is 400 LEAD_ACTIVITY_SECTOR_INVALID. The open "
                + "company prospects are rescored in the background. "
                + "Requires permission: lead:dispatching-rule:manage, like the temperature thresholds.")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces<KeyAccountSettingsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithTenantHeader();

        return group;
    }
}
