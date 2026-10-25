namespace Sankore.Api.Features.Retention;

using Hangfire;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>
/// Runs every module's <see cref="ITechnicalPurge"/>, every night, for every tenant at once — the
/// durations are the platform's (<see cref="TechnicalRetention"/>), not a tenant's (owner,
/// 2026-10-24). Business data is not touched here: leads, clients and KYC follow their own rules,
/// and are erased only on a person's decision.
///
/// <para>
/// One purge at a time, each in its own scope: a purge that fails is logged and the others go on —
/// a broken notifications table must not keep sessions forever. The job fails at the end when one
/// did, so the Hangfire dashboard shows it red instead of a green run that purged half.
/// </para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
public sealed class TechnicalPurgeJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<TechnicalPurgeJob> logger)
{
    public const string RecurringJobId = "technical-purge";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        IReadOnlyList<Type> purgeTypes;
        using (var scope = scopeFactory.CreateScope())
            purgeTypes = scope.ServiceProvider.GetServices<ITechnicalPurge>().Select(p => p.GetType()).ToList();

        var failed = new List<string>();
        foreach (var type in purgeTypes)
        {
            using var scope = scopeFactory.CreateScope();
            var purge = scope.ServiceProvider.GetServices<ITechnicalPurge>().First(p => p.GetType() == type);
            try
            {
                var count = await purge.PurgeAsync(now, ct);
                logger.LogInformation("Technical purge: {Name} — {Count} row(s).", purge.Name, count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(purge.Name);
                logger.LogError(ex, "Technical purge: {Name} failed; the other purges go on.", purge.Name);
            }
        }

        if (failed.Count > 0)
            throw new InvalidOperationException($"Technical purge failed for: {string.Join(", ", failed)}.");
    }
}
