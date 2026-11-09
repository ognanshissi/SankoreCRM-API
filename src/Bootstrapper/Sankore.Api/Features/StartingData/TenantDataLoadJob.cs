namespace Sankore.Api.Features.StartingData;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Transactions;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Api.Infrastructure;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

/// <summary>
/// Loads the starting data a platform administrator asked for (<c>docs/donnees-demarrage-demo.md</c>),
/// on the data wipe's circuit: the registry records the request, this job pulls the pending ones with
/// the service key, takes each one with a conditional start, loads, and reports what it created or why
/// it failed.
///
/// <para>
/// For a tenant created while the API runs, it also does what only a restart did before: the tenant's
/// system account (created with a random password nobody knows — its owner sets one with « Mot de
/// passe oublié »), the modules' default seeders (settings, dispatching rule, task types, KYC
/// circuit…) and the tenant's recurring jobs.
/// </para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
public sealed class TenantDataLoadJob(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IRecurringJobManager recurringJobs,
    JobStorage jobStorage,
    ILogger<TenantDataLoadJob> logger)
{
    public const string RecurringJobId = "tenant-data-loads";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PendingLoad(Guid Id, Guid TenantId, string Kind);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var registry = httpClientFactory.CreateClient(TenantStoreServiceCollectionExtensions.HttpClientName);
        var pending = await registry.GetFromJsonAsync<List<PendingLoad>>("api/v1/data-loads/pending", Json, ct) ?? [];

        foreach (var load in pending)
        {
            var (tenant, rootEmail) = await FreshTenantAsync(registry, load.TenantId, ct);
            var (kind, wipeFirst) = DataLoadGate.Parse(load.Kind);
            if (DataLoadGate.Refusal(tenant, kind) is { } refusal)
            {
                await registry.PostAsJsonAsync($"api/v1/data-loads/{load.Id}/fail", new { Error = refusal }, Json, ct);
                logger.LogError("Data load {LoadId} refused for tenant {TenantId}: {Refusal}", load.Id, load.TenantId, refusal);
                continue;
            }

            var started = await registry.PostAsync($"api/v1/data-loads/{load.Id}/start", content: null, ct);
            if (!started.IsSuccessStatusCode) continue; // taken by another instance

            logger.LogInformation("Data load {LoadId}: {Kind} for tenant {TenantId} ({Name}).", load.Id, kind, tenant!.Id, tenant.Name);
            var summary = new Dictionary<string, int>();
            var current = "Compte système";
            try
            {
                var (systemUserId, created) = await EnsureSystemUserAsync(tenant, rootEmail, ct);
                if (created) summary["Compte système"] = 1;

                if (wipeFirst)
                {
                    current = "Effacement des dossiers";
                    var erased = await WipeRecordsAsync(tenant.Id, ct);
                    if (erased > 0) summary["Lignes effacées"] = erased;
                }

                current = "Paramétrage par défaut";
                await using (var scope = scopeFactory.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<TenantProvisioner>().SeedAllAsync(ct);

                var request = new StartingDataRequest(tenant.Id, tenant.Name, kind!.Value, systemUserId);
                foreach (var step in StepsInOrder())
                {
                    current = step.Module;
                    using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                    // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
                    using var bg = BackgroundJobContext.SetScope(tenant.Id, systemUserId, "SYSTEM");
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var stepOf = scope.ServiceProvider.GetServices<ITenantStartingData>().First(s => s.Module == step.Module);
                    // Each command in a scope of its own (ScopedSender): the step's scope keeps its reads.
                    var stepServices = new StartingDataServices(scope.ServiceProvider, new ScopedSender(scopeFactory));
                    foreach (var (label, count) in await stepOf.LoadAsync(request, stepServices, ct))
                        if (count > 0) summary[label] = summary.GetValueOrDefault(label) + count;
                    suppressed.Complete();
                }

                current = "Tâches récurrentes";
                var sink = new PauseAwareJobSink(recurringJobs, new RecurringJobPauseStore(jobStorage, recurringJobs).GetPausedIds());
                TenantRecurringJobs.Register(tenant.Id, sink);

                await registry.PostAsJsonAsync($"api/v1/data-loads/{load.Id}/complete",
                    new { Summary = JsonSerializer.Serialize(summary, Json) }, Json, ct);
                logger.LogInformation("Data load {LoadId} done for tenant {TenantId}: {Summary}.", load.Id, tenant.Id, summary);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Data load {LoadId} failed in {Step} for tenant {TenantId}.", load.Id, current, tenant.Id);
                await registry.PostAsJsonAsync($"api/v1/data-loads/{load.Id}/fail",
                    new { Error = $"{current} : {ex.GetType().Name} — {ex.Message}" }, Json, ct);
            }
        }
    }

    /// <summary>
    /// « Recharger la démo »: exactly what « Effacer les données de test » empties — every module's
    /// <see cref="Sankore.Shared.Infrastructure.Retention.ITenantDataWipe"/>, configuration and accounts
    /// kept — so the demo steps then find no record and create them afresh. The gate above has checked,
    /// on the registry read fresh, that the institution is a test one.
    /// </summary>
    private async Task<int> WipeRecordsAsync(Guid tenantId, CancellationToken ct)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        await using var scope = scopeFactory.CreateAsyncScope();
        var total = 0;
        foreach (var module in scope.ServiceProvider.GetServices<Sankore.Shared.Infrastructure.Retention.ITenantDataWipe>())
            total += await module.WipeAsync(tenantId, ct);
        logger.LogWarning("Demo reload: {Rows} rows erased in TEST tenant {TenantId}.", total, tenantId);
        suppressed.Complete();
        return total;
    }

    /// <summary>The steps by order, resolved once to know them; each runs in its own scope.</summary>
    private IReadOnlyList<ITenantStartingData> StepsInOrder()
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetServices<ITenantStartingData>().OrderBy(s => s.Order).ToList();
    }

    /// <summary>
    /// The tenant's system account, created when missing through the same <see cref="RegisterCommand"/>
    /// as <c>POST users/create-root</c> and the start-up seeder. Its password is random and shown to
    /// nobody: no secret leaves this job, and the owner of the root address sets their own with
    /// « Mot de passe oublié ».
    /// </summary>
    private async Task<(Guid UserId, bool Created)> EnsureSystemUserAsync(TenantInfo tenant, string? rootEmail, CancellationToken ct)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        using var bg = BackgroundJobContext.SetScope(tenant.Id, Guid.Empty, "SYSTEM");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdministrationDbContext>();

        var existing = await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenant.Id && u.IsSuperUser && u.AccountType == UserAccountType.System)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return (id, false);

        if (string.IsNullOrWhiteSpace(rootEmail))
            throw new InvalidOperationException("L'institution n'a pas d'adresse d'administrateur dans le registre.");

        var password = RandomPassword();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            new RegisterCommand(rootEmail.Trim(), password, password, "Administrateur", tenant.Name, tenant.Id), ct);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);

        suppressed.Complete();
        return (result.Value.UserId, true);
    }

    /// <summary>Thirty-two random characters, with a digit, both cases and a symbol, whatever the policy asks.</summary>
    internal static string RandomPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var chars = Enumerable.Range(0, 28).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return new string(chars.ToArray()) + "Aa9!";
    }

    private async Task<(TenantInfo? Tenant, string? RootEmail)> FreshTenantAsync(HttpClient registry, Guid tenantId, CancellationToken ct)
    {
        try
        {
            // The registry's full record: TenantInfo, plus the root address it does not carry.
            var record = await registry.GetFromJsonAsync<JsonElement>($"api/v1/tenants/{tenantId}", Json, ct);
            var root = record.TryGetProperty("rootUserEmail", out var email) ? email.GetString() : null;
            return (record.Deserialize<TenantInfo>(Json), root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Data load: tenant {TenantId} could not be read from the registry.", tenantId);
            return (null, null);
        }
    }
}

/// <summary>The API's own check of a load, against the registry read fresh — the registry's is not trusted alone.</summary>
internal static class DataLoadGate
{
    /// <summary>
    /// The registry's kind: « DemoReload » is the demo, after the records are erased — the steps only
    /// ever see Starter or Demo.
    /// </summary>
    public static (StartingDataKind? Kind, bool WipeFirst) Parse(string? kind)
        => string.Equals(kind?.Trim(), "DemoReload", StringComparison.OrdinalIgnoreCase) ? (StartingDataKind.Demo, true)
         : Enum.TryParse<StartingDataKind>(kind?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? (parsed, false)
         : (null, false);

    public static string? Refusal(TenantInfo? tenant, StartingDataKind? kind)
        => tenant is null ? "TENANT_NOT_FOUND : l'institution est introuvable dans le registre."
         : kind is null ? "DATA_LOAD_KIND_UNKNOWN : type de chargement inconnu."
         : tenant.ParentTenantId is not null ? "DATA_LOAD_ON_TEST_TWIN : un jumeau « Mode test » reçoit le paramétrage de sa production."
         : kind == StartingDataKind.Demo && !tenant.IsTest ? "DEMO_REQUIRES_TEST_TENANT : la démo ne se charge que dans une institution de test."
         : null;
}
