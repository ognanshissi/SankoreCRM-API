namespace Sankore.Api.Features.TestMode;

using Hangfire;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Repartir de la production » (sandbox lot 5): the test mode takes production's configuration
/// again — products, workflows, project templates, lead sources, webhooks — and, when asked, is
/// emptied of its trial data first (leads, clients, projects, tasks…; what a test-tenant wipe empties).
///
/// <para>
/// It lives in the host because it needs every module: the wipes (<see cref="ITenantDataWipe"/>) and
/// the copiers (<see cref="IConfigurationCopier"/>), run in their <see cref="IConfigurationCopier.Order"/>
/// so a project template finds its product already there. Each object goes through the same copy as
/// « Copier en production », the other way: production's save commands replayed in the twin, keys and
/// secrets never crossing (a source gets its own <c>pk_test_</c>, a webhook its own <c>whsec_test_</c>
/// and arrives switched off). Objects only the test mode has are kept. A job, not a request: a
/// catalogue is dozens of objects and several commands each; the user is told on the bell.
/// </para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
[AutomaticRetry(Attempts = 0)]
public sealed class RefreshTestTwinJob(
    IServiceScopeFactory scopes,
    ITenantStore tenants,
    ILogger<RefreshTestTwinJob> logger)
{
    public async Task ExecuteAsync(Guid twinId, Guid productionId, Guid userId, string userName, bool wipeData)
    {
        var ct = CancellationToken.None;

        // The registry, not the caller, says this is the twin of that production: a wipe follows.
        var twin = await tenants.GetAsync(twinId, ct);
        if (twin is not { IsTest: true } || twin.ParentTenantId != productionId)
        {
            logger.LogError("Refresh refused: {TwinId} is not the test twin of {ProductionId}.", twinId, productionId);
            return;
        }

        // Before the scope: ITenantContext and ICurrentUser are built from it — the twin, as the user.
        using var bg = BackgroundJobContext.SetScope(twinId, userId, userName);
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var wiped = 0;
        if (wipeData)
            foreach (var module in services.GetServices<ITenantDataWipe>())
                wiped += await module.WipeAsync(twinId, ct);

        var copied = 0;
        List<(string Kind, string Reason)> failures = [];
        foreach (var copier in services.GetServices<IConfigurationCopier>().OrderBy(c => c.Order))
        {
            foreach (var id in await copier.ListAsync(productionId, ct))
            {
                try
                {
                    var result = await copier.CopyAsync(new ConfigurationCopy(productionId, id, twinId, userId, userName, DryRun: false, ToProduction: false), ct);
                    if (result.IsSuccess) copied++;
                    else failures.Add((copier.Kind, result.Error ?? ""));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Refresh of twin {TwinId}: copying {Kind} {Id} failed.", twinId, copier.Kind, id);
                    failures.Add((copier.Kind, ""));
                }
            }
        }

        logger.LogWarning("Test twin {TwinId} refreshed from {ProductionId}: {Copied} copied, {Failed} failed, {Wiped} rows wiped.",
            twinId, productionId, copied, failures.Count, wiped);

        var body = failures.Count == 0
            ? $"{copied} éléments de configuration repris de la production."
            : $"{copied} éléments repris de la production, {failures.Count} non repris : {Describe(failures)}.";
        if (wipeData) body += " Les données d’essai ont été vidées.";

        await services.GetRequiredService<INotificationsModule>().NotifyInAppAsync(new InAppNotificationRequest(
            twinId, userId, "tenant.test-mode.refreshed", "Mode test rafraîchi", body, "/",
            $"test-mode.refreshed:{twinId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"), ct);
    }

    private static readonly Dictionary<string, string> KindLabels = new()
    {
        ["product"] = "produit",
        ["workflow"] = "workflow",
        ["project-template"] = "gabarit de projet",
        ["lead-source"] = "source de leads",
        ["webhook"] = "webhook",
    };

    /// <summary>Why, in words the bell's reader understands; a code nobody translated stays a code.</summary>
    private static readonly Dictionary<string, string> Reasons = new()
    {
        ["PROJECT_STEP_QUESTIONNAIRE_FORM_MISSING"] = "il exige un questionnaire publié, absent du mode test",
        ["PROMOTION_DEPENDENCY_MISSING"] = "un élément lié manque",
        [""] = "erreur inattendue, voir les journaux",
    };

    /// <summary>« 2 gabarit de projet (il exige un questionnaire publié…) » — grouped, at most five groups.</summary>
    internal static string Describe(IEnumerable<(string Kind, string Reason)> failures)
        => string.Join(" ; ", failures
            .GroupBy(f => f)
            .Take(5)
            .Select(g => $"{g.Count()} {KindLabels.GetValueOrDefault(g.Key.Kind, g.Key.Kind)} ({Reasons.GetValueOrDefault(g.Key.Reason, g.Key.Reason)})"));
}

public sealed record RefreshTestModeRequest(bool WipeData);

public static class RefreshTestModeEndpoint
{
    /// <summary>Relayed as the problem's <c>detail</c>.</summary>
    public const string TestModeRequired = "TEST_MODE_REQUIRED";

    public static IEndpointRouteBuilder MapRefreshTestMode(this IEndpointRouteBuilder group)
    {
        group.MapPost("auth/test-mode/refresh", (RefreshTestModeRequest body, ICurrentUser me, IBackgroundJobClient jobs) =>
            {
                // From inside the test mode: the twin and its production come from the token.
                if (me.TenantId == me.TeamTenantId)
                    return Results.Problem(detail: TestModeRequired, statusCode: StatusCodes.Status409Conflict);

                var (twin, production, user, name) = (me.TenantId, me.TeamTenantId, me.Id, me.DisplayName);
                jobs.Enqueue<RefreshTestTwinJob>(j => j.ExecuteAsync(twin, production, user, name, body.WipeData));
                return Results.Accepted();
            })
            .WithTags("Auth")
            .WithName("RefreshTestMode")
            .WithSummary("Start again from production's configuration, in the test mode")
            .WithDescription(
                "From « Mode test »: copies production's products, workflows, project templates, lead sources and "
                + "webhooks into the test mode, in the background — and empties its trial data first when wipeData. "
                + "Keys and secrets never cross. The user is told on the bell. Needs tenant:test-mode and the edit "
                + "right of each kind copied. 409 TEST_MODE_REQUIRED.")
            // The copies replay under the System role: the caller must hold every edit right they
            // exercise — the same pairing as « Copier en production » — not only the test mode.
            .RequireAuthorization(Permissions.CanUseTestMode.Code)
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .RequireAuthorization(Permissions.CanCreateWorkflow.Code)
            .RequireAuthorization(Permissions.CanManagePipelineStages.Code)
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces(StatusCodes.Status202Accepted);
        return group;
    }
}
