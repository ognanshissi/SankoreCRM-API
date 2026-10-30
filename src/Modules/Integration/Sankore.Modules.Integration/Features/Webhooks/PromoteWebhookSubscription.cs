namespace Sankore.Modules.Integration.Features.Webhooks;

using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » for an outbound webhook (sandbox lot 4), through the ordinary save
/// command. The production subscription is the one with the same NAME: its address, description
/// and events take the test mode's, its secret and its on/off state stay. A subscription new to
/// production gets its own <c>whsec_</c> secret — returned ONCE in the result — and is created
/// SWITCHED OFF: production must not start signing events with a secret the partner does not hold.
/// </summary>
// Audited like any save — in the test mode, by whoever copied; the replayed commands are audited
// again in production. ProductionReplay suppresses this command's transaction for the replay.
internal sealed record PromoteWebhookSubscriptionCommand(Guid SubscriptionId, bool DryRun)
    : IRequest<Result<PromotionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscription";
    public string? ResourceId => SubscriptionId.ToString();
}

internal sealed class PromoteWebhookSubscriptionHandler(ProductionReplay production, WebhookSubscriptionCopier copier)
    : IRequestHandler<PromoteWebhookSubscriptionCommand, Result<PromotionResult>>
{
    public Task<Result<PromotionResult>> Handle(PromoteWebhookSubscriptionCommand cmd, CancellationToken ct)
        => production.InTestMode
            ? copier.CopyAsync(production.ToProduction(cmd.SubscriptionId, cmd.DryRun), ct)
            : Task.FromResult(Result.Fail<PromotionResult>(PromotionErrors.TestModeRequired));
}

/// <summary>
/// One outbound webhook from one tenant to another, matched by NAME. A subscription new to the
/// target is created SWITCHED OFF with the target's own secret — returned once: neither mode may
/// start signing events with a secret the partner does not hold, nor a sandbox start calling a
/// partner's production address on its own. An existing one keeps its secret and its state.
/// </summary>
internal sealed class WebhookSubscriptionCopier(IntegrationDbContext db, IServiceScopeFactory scopes) : IConfigurationCopier
{
    public string Kind => "webhook";

    public int Order => 50;

    public async Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.WebhookSubscriptions.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).Select(s => s.Id).ToListAsync(ct);

    public async Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
    {
        var source = await db.WebhookSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == copy.SourceId && s.TenantId == copy.SourceTenantId, ct);
        if (source is null) return Result.Fail<PromotionResult>(PromotionErrors.SourceNotFound);

        var target = await db.WebhookSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == copy.TargetTenantId && s.Name == source.Name)
            .Select(s => new { s.Id })
            .FirstOrDefaultAsync(ct);

        var action = target is null ? PromotionActions.Created : PromotionActions.Updated;
        var where = copy.ToProduction ? "de production" : "du mode test";
        List<string> notes = target is null
            ? [$"Créé désactivé, avec son propre secret {where} : remettez-le au partenaire, puis activez le webhook."]
            : ["Le secret et l’état (actif ou non) déjà en place sont conservés."];

        if (copy.DryRun) return Result.Ok(new PromotionResult(action, target?.Id, [], notes));

        var copied = await TenantReplay.RunAsync<Result<(Guid Id, string? Secret)>>(scopes, copy.TargetTenantId, copy.UserId, copy.UserName, async (sender, _) =>
        {
            var saved = await sender.Send(new SaveWebhookSubscriptionCommand(target?.Id, source.Name, source.Url, source.Description, source.Events), ct);
            if (saved.IsFailure) return Result.Fail<(Guid, string?)>(saved.Error!);

            var id = saved.Value.Subscription.Id;
            if (target is not null) return Result.Ok<(Guid, string?)>((id, null));

            var off = await sender.Send(new SetWebhookSubscriptionActiveCommand(id, false), ct);
            return off.IsFailure ? Result.Fail<(Guid, string?)>(off.Error!) : Result.Ok<(Guid, string?)>((id, saved.Value.Secret));
        });

        return copied.IsFailure
            ? Result.Fail<PromotionResult>(copied.Error!)
            : Result.Ok(new PromotionResult(action, copied.Value.Id, [], notes, copied.Value.Secret));
    }
}

internal static class PromoteWebhookSubscriptionEndpoint
{
    public static IEndpointRouteBuilder MapPromoteWebhookSubscription(this IEndpointRouteBuilder group)
    {
        group.MapPromote("WebhookSubscription", Permissions.CanManageWebhooks.Code,
            (id, dryRun, sender, ct) => sender.Send(new PromoteWebhookSubscriptionCommand(id, dryRun), ct));
        return group;
    }
}
