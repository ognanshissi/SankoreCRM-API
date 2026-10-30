namespace Sankore.Shared.Infrastructure.TestMode;

using System.Transactions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » (sandbox lot 4): what a copy did, or — <c>dryRun</c> — would do.
/// </summary>
/// <param name="Action"><c>Created</c> or <c>Updated</c> in production.</param>
/// <param name="ProductionId">The production object; null on a dry run that would create it.</param>
/// <param name="Missing">
/// Linked configuration production lacks. A <c>Blocking</c> one stops the copy; the front copies the
/// missing ones first, once the user confirmed, then this one.
/// </param>
/// <param name="Notes">What was deliberately not carried over (secrets, a back-office link…), in French.</param>
/// <param name="Secret">A secret production issued for the copy, shown ONCE (a webhook's signing secret).</param>
public sealed record PromotionResult(
    string Action,
    Guid? ProductionId,
    IReadOnlyList<PromotionDependency> Missing,
    IReadOnlyList<string> Notes,
    string? Secret = null);

/// <param name="Kind"><c>product</c>, <c>workflow</c>… — which copy the front calls first.</param>
/// <param name="TestId">The object in the test mode, to copy first; empty when it does not exist there either.</param>
public sealed record PromotionDependency(string Kind, Guid TestId, string Label, bool Blocking);

public static class PromotionActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
}

/// <summary>Relayed as the problem's <c>detail</c>: never rename one.</summary>
public static class PromotionErrors
{
    /// <summary>Copying to production is done from « Mode test », where the source lives.</summary>
    public const string TestModeRequired = "PROMOTION_TEST_MODE_REQUIRED";
    public const string SourceNotFound = "PROMOTION_SOURCE_NOT_FOUND";
    /// <summary>A blocking dependency is missing in production (a project template's product).</summary>
    public const string DependencyMissing = "PROMOTION_DEPENDENCY_MISSING";
}

/// <summary>
/// Runs a module's ordinary save commands IN PRODUCTION, as the user, from a « Mode test »
/// request (sandbox lot 4). The copy goes through the same validation, invariants, versioning and
/// audit as an edit made in production — nothing is written behind the editors' back.
///
/// <para>
/// A <see cref="BackgroundJobContext"/> naming the live tenant takes precedence over the request's
/// token for every service resolved in the inner scope; the user is the caller, under the System
/// role (the route has checked the permissions). The work runs outside the ambient transaction:
/// each replayed command opens its own, and a second context enlisting in the caller's would be
/// promoted to a distributed transaction Npgsql does not support. A copy is therefore not atomic
/// across its commands — it is idempotent instead: run again, it updates what the first run created.
/// </para>
/// </summary>
public sealed class ProductionReplay(IServiceScopeFactory scopes, ICurrentUser me)
{
    public bool InTestMode => me.TenantId != me.TeamTenantId;

    /// <summary>The live tenant the copy lands in.</summary>
    public Guid ProductionTenantId => me.TeamTenantId;

    /// <summary>The copy of <paramref name="sourceId"/>, from the caller's test mode to production.</summary>
    public ConfigurationCopy ToProduction(Guid sourceId, bool dryRun)
        => new(me.TenantId, sourceId, me.TeamTenantId, me.Id, me.DisplayName, dryRun, ToProduction: true);

    public Task<T> RunAsync<T>(Func<ISender, IServiceProvider, Task<T>> work)
    {
        if (!InTestMode) throw new InvalidOperationException("A copy to production starts from test mode.");
        return TenantReplay.RunAsync(scopes, me.TeamTenantId, me.Id, me.DisplayName, work);
    }
}

/// <summary>The replay itself, into any tenant: production for a promotion, the twin for a refresh.</summary>
public static class TenantReplay
{
    public static async Task<T> RunAsync<T>(
        IServiceScopeFactory scopes, Guid targetTenantId, Guid userId, string userName,
        Func<ISender, IServiceProvider, Task<T>> work)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        T result;
        using (BackgroundJobContext.SetScope(targetTenantId, userId, userName))
        {
            await using var scope = scopes.CreateAsyncScope();
            result = await work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider);
        }
        suppressed.Complete();
        return result;
    }
}

/// <summary>
/// One configuration object to copy from one tenant to another (sandbox lots 4 and 5).
/// <paramref name="ToProduction"/> is the direction: test mode → production (« Copier en
/// production »), or production → test mode (« Repartir de la production »).
/// </summary>
public sealed record ConfigurationCopy(
    Guid SourceTenantId,
    Guid SourceId,
    Guid TargetTenantId,
    Guid UserId,
    string UserName,
    bool DryRun,
    bool ToProduction);

/// <summary>
/// The copy of one kind of configuration, both ways. A module registers one per kind it owns; the
/// refresh of a test mode runs them all in <see cref="Order"/> — what others depend on first
/// (products before project templates, workflows before them too).
/// </summary>
public interface IConfigurationCopier
{
    /// <summary><c>product</c>, <c>workflow</c>… — the same word as <see cref="PromotionDependency.Kind"/>.</summary>
    string Kind { get; }

    int Order { get; }

    /// <summary>What a refresh copies from <paramref name="tenantId"/>: its objects of this kind.</summary>
    Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct);

    Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct);
}

public static class PromotionEndpoints
{
    /// <summary>
    /// <c>POST {group}/{id}/promote?dryRun=</c>: refused outside test mode, and only for whoever holds
    /// both the promotion permission and the right to edit that kind of configuration.
    /// </summary>
    public static RouteHandlerBuilder MapPromote(
        this IEndpointRouteBuilder group, string kind, string managePermission,
        Func<Guid, bool, ISender, CancellationToken, Task<Result<PromotionResult>>> promote)
        => group.MapPost("{id:guid}/promote", async (Guid id, bool? dryRun, HttpContext http, ISender sender, CancellationToken ct) =>
            {
                if (!TestModeGuard.IsTestMode(http))
                    return Results.Problem(detail: PromotionErrors.TestModeRequired, statusCode: StatusCodes.Status409Conflict);

                var result = await promote(id, dryRun ?? false, sender, ct);
                if (result.IsSuccess) return Results.Ok(result.Value);

                return Results.Problem(detail: result.Error, statusCode: result.Error switch
                {
                    PromotionErrors.SourceNotFound => StatusCodes.Status404NotFound,
                    PromotionErrors.DependencyMissing => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status422UnprocessableEntity,
                });
            })
            .WithName($"Promote{kind}")
            .WithSummary($"Copy a {kind} from test mode to production")
            .WithDescription("From « Mode test »: creates or updates the production counterpart through the "
                + "ordinary save commands. dryRun=true only answers what would happen and what linked "
                + "configuration production lacks. 409 PROMOTION_TEST_MODE_REQUIRED / PROMOTION_DEPENDENCY_MISSING; "
                + "422 with the save command's own code.")
            .RequireAuthorization(Permissions.CanPromoteTestConfiguration.Code)
            .RequireAuthorization(managePermission)
            .Produces<PromotionResult>();
}
