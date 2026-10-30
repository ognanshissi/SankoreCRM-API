namespace Sankore.Modules.Leads.Features.LeadSources;

using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » for a lead source (sandbox lot 4), through the ordinary create/update
/// commands. The production source is the one with the same CODE.
///
/// <para>
/// <b>What does not cross.</b> The keys and secrets are each mode's own: a new production source
/// gets its own <c>pk_live_</c> key, an existing one keeps its key and its status (a live source
/// stays live, a new one starts as a draft to activate there), and the vault references in its
/// settings are the target's own — never the source's, which point at secrets production cannot
/// read. The platform connection (Meta, Google…) is production's too. The default dispatching rule
/// is matched by NAME, else dropped with a note: rules are not copied in this lot.
/// </para>
/// </summary>
// Audited like any save — in the test mode, by whoever copied; the replayed commands are audited
// again in production. ProductionReplay suppresses this command's transaction for the replay.
internal sealed record PromoteLeadSourceCommand(Guid SourceId, bool DryRun)
    : IRequest<Result<PromotionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId => SourceId.ToString();
}

internal sealed class PromoteLeadSourceHandler(ProductionReplay production, LeadSourceCopier copier)
    : IRequestHandler<PromoteLeadSourceCommand, Result<PromotionResult>>
{
    public Task<Result<PromotionResult>> Handle(PromoteLeadSourceCommand cmd, CancellationToken ct)
        => production.InTestMode
            ? copier.CopyAsync(production.ToProduction(cmd.SourceId, cmd.DryRun), ct)
            : Task.FromResult(Result.Fail<PromotionResult>(PromotionErrors.TestModeRequired));
}

/// <summary>One lead source from one tenant to another — see the command for what never crosses.</summary>
internal sealed class LeadSourceCopier(LeadsDbContext db, IServiceScopeFactory scopes) : IConfigurationCopier
{
    public string Kind => "lead-source";

    public int Order => 40;

    /// <summary>The system sources each tenant has of its own are not copied.</summary>
    public async Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.LeadSourceConfigs.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && !s.IsSystem && s.Status != LeadSourceStatus.Archived)
            .Select(s => s.Id).ToListAsync(ct);

    public async Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == copy.SourceId && s.TenantId == copy.SourceTenantId, ct);
        if (source is null || source.IsSystem) return Result.Fail<PromotionResult>(PromotionErrors.SourceNotFound);

        var target = await db.LeadSourceConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == copy.TargetTenantId && s.Code == source.Code, ct);
        var where = copy.ToProduction ? "en production" : "dans le mode test";

        List<string> notes = [];
        Guid? rule = null;
        if (source.DefaultDispatchingRuleId is { } sourceRule)
        {
            var name = await db.DispatchingRules.IgnoreQueryFilters()
                .Where(r => r.Id == sourceRule && r.TenantId == copy.SourceTenantId).Select(r => r.Name).FirstOrDefaultAsync(ct);
            rule = name is null ? null : await db.DispatchingRules.IgnoreQueryFilters()
                .Where(r => r.TenantId == copy.TargetTenantId && r.Name == name).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
            if (rule is null)
                notes.Add($"La règle de dispatching « {name} » n’existe pas {where} : la source utilisera la règle par défaut.");
        }
        if (HasSecrets(source.Settings))
            notes.Add($"Les secrets et identifiants ne sont pas copiés : ceux déjà enregistrés {where} restent, à saisir s’ils manquent.");
        if (target is null)
            notes.Add($"La source est créée en brouillon, avec sa propre clé : activez-la {where}.");

        var action = target is null ? PromotionActions.Created : PromotionActions.Updated;
        if (copy.DryRun) return Result.Ok(new PromotionResult(action, target?.Id, [], notes));

        var settings = WithSecretsOf(source.Settings, target?.Mode == source.Mode ? target.Settings : null);

        var copied = await TenantReplay.RunAsync<Result<Guid>>(scopes, copy.TargetTenantId, copy.UserId, copy.UserName, async (sender, _) =>
        {
            if (target is null)
                return await sender.Send(new CreateLeadSourceCommand(
                    copy.TargetTenantId, source.Code, source.Label, source.ChannelType, source.DisplayOrder, source.Mode, source.Description,
                    settings, PlatformConnectionId: null, source.DedupWindowDays,
                    source.CostPerLead?.Amount, source.CostPerLead?.Currency, source.DefaultAgencyId, rule), ct);

            var updated = await sender.Send(new UpdateLeadSourceCommand(
                target.Id, target.Version, source.Label, source.DisplayOrder, source.Description, settings, target.PlatformConnectionId,
                source.DedupWindowDays, source.CostPerLead?.Amount, source.CostPerLead?.Currency, source.DefaultAgencyId, rule), ct);
            return updated.IsFailure ? Result.Fail<Guid>(updated.Error!) : Result.Ok(target.Id);
        });

        return copied.IsFailure
            ? Result.Fail<PromotionResult>(copied.Error!)
            : Result.Ok(new PromotionResult(action, copied.Value, [], notes));
    }

    private static bool HasSecrets(SourceSettings? settings) => settings is
        ServerWebhookSettings { SignatureCredentialVaultRef: not null }
        or ScheduledPullSettings { AuthCredentialVaultRef: not null }
        or PlatformSettings { OAuthCredentialVaultRef: not null };

    /// <summary>The source settings, carrying the target's vault references (or none) in place of the source's.</summary>
    internal static SourceSettings? WithSecretsOf(SourceSettings? source, SourceSettings? target) => source switch
    {
        ServerWebhookSettings s => s with { SignatureCredentialVaultRef = (target as ServerWebhookSettings)?.SignatureCredentialVaultRef },
        ScheduledPullSettings s => s with { AuthCredentialVaultRef = (target as ScheduledPullSettings)?.AuthCredentialVaultRef },
        PlatformSettings s => s with { OAuthCredentialVaultRef = (target as PlatformSettings)?.OAuthCredentialVaultRef },
        _ => source,
    };
}

internal static class PromoteLeadSourceEndpoint
{
    public static IEndpointRouteBuilder MapPromoteLeadSource(this IEndpointRouteBuilder group)
    {
        group.MapPromote("LeadSource", Permissions.CanManageLeadSources.Code,
            (id, dryRun, sender, ct) => sender.Send(new PromoteLeadSourceCommand(id, dryRun), ct))
            .WithTags("LeadSources");
        return group;
    }
}
