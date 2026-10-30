namespace Sankore.Shared.Infrastructure.Tenants;

using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// One module's per-tenant starting data — settings rows, the default dispatching rule, the system
/// task types, the KYC circuit… — as run at start-up for every active tenant. Registered so a
/// tenant created while the host runs (a « Mode test » twin, sandbox lot 2) gets the same start
/// without waiting for a restart. Every seeder is idempotent and inserts only what is missing.
/// </summary>
public interface ITenantSeeder
{
    string Module { get; }

    /// <summary>Runs the module's seeders, over every active tenant the store lists.</summary>
    Task SeedAsync(IServiceProvider services, CancellationToken ct);
}

public sealed class TenantSeeder(string module, Func<IServiceProvider, CancellationToken, Task> seed) : ITenantSeeder
{
    public string Module { get; } = module;

    public Task SeedAsync(IServiceProvider services, CancellationToken ct) => seed(services, ct);
}

/// <summary>
/// Runs every registered <see cref="ITenantSeeder"/>, each in its own scope and outside any
/// ambient transaction — they write through other modules' contexts, which a request's
/// TransactionScope would try to promote. A module that fails is logged and the others run:
/// the restart re-seeds anyway, and a sandbox without one default must not block the others.
/// </summary>
public sealed class TenantProvisioner(
    IEnumerable<ITenantSeeder> seeders, IServiceScopeFactory scopes, ILogger<TenantProvisioner> logger)
{
    public async Task SeedAllAsync(CancellationToken ct)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        foreach (var seeder in seeders)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await seeder.SeedAsync(scope.ServiceProvider, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Seeding module {Module} for a new tenant failed; the next start-up retries it.", seeder.Module);
            }
        }

        suppressed.Complete();
    }
}
