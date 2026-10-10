namespace Sankore.Modules.Integration.Tests.Features.KycLimits;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

/// <summary>
/// The scaffolding the INT-22 suites share: an InMemory context that <b>enforces the ledger's
/// unique index</b>, a frozen clock, snapshot and limits factories, and a reader that pulls events
/// back out of the outbox.
/// </summary>
internal static class KycLimitsTestContext
{
    internal static readonly DateTimeOffset March = new(2026, 3, 11, 2, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset April = new(2026, 4, 2, 2, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A context over a named InMemory database. <paramref name="ledgerIndex"/> is what makes the
    /// dedup tests mean anything — see <see cref="LedgerUniqueIndexInterceptor"/>.
    /// </summary>
    internal static IntegrationDbContext NewDb(
        Guid tenantId, string databaseName, LedgerUniqueIndexInterceptor? ledgerIndex = null)
    {
        var builder = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(databaseName);

        if (ledgerIndex is not null) builder.AddInterceptors(ledgerIndex);

        return new IntegrationDbContext(builder.Options, new FixedTenantContext(tenantId));
    }

    /// <summary>
    /// A service provider shaped like the API's, for the orchestrator that creates its own scope.
    /// <c>ITenantContext</c> is registered exactly as <c>Program.cs</c> registers it — reading the
    /// ambient background context — because that registration is the whole reason
    /// <c>BackgroundJobContext.SetScope</c> has to happen before <c>CreateScope</c>.
    /// </summary>
    internal static ServiceProvider NewProvider(string databaseName)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(March));

        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException(
                "No ambient tenant: the job created its scope before establishing one."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(databaseName), ServiceLifetime.Scoped);

        return services.BuildServiceProvider();
    }

    /// <summary>A snapshot carrying the two figures the watch compares, and nothing else.</summary>
    internal static CbsSnapshot Snapshot(
        Guid tenantId, Guid crmCustomerId, decimal totalBalance, decimal monthlyFlow)
    {
        var clock = new FixedClock(March);
        var snapshot = CbsSnapshot.Create(
            tenantId, crmCustomerId, Guid.Parse("11111111-1111-1111-1111-111111111111"), clock);

        snapshot.Update("[]", "[]", totalBalance, monthlyFlow, kycLevelInCbs: null, clock);

        return snapshot;
    }

    /// <summary>
    /// The ceilings M02 would hand back for a simplified-tier customer, with the factory defaults
    /// of <c>KycSettingKeys</c> — 250 000, 500 000, 30 days, 80 %. Every test that depends on a
    /// figure overrides it here, so no production file is the source of these numbers.
    /// </summary>
    internal static KycLimits Capped(
        decimal maxBalance = 250_000m,
        decimal maxFlow = 500_000m,
        int windowDays = 30,
        int alertPct = 80)
        => new("Simplified", IsCapped: true, maxBalance, maxFlow, windowDays, alertPct);

    /// <summary>
    /// A full-KYC file. The amounts are deliberately non-zero and small: the contract documents
    /// them as meaningless when <c>IsCapped</c> is false, so a watch that read them anyway would
    /// alert on this customer — which is what the uncapped test pins.
    /// </summary>
    internal static KycLimits Uncapped()
        => new("Full", IsCapped: false, MaxBalance: 1m, MaxFlow: 1m, WindowDays: 30, AlertPct: 80);

    /// <summary>
    /// The real outbox publisher over the given context — not a recording double. INT-22 requires
    /// the event to travel through the module's outbox in the ledger row's own transaction, and a
    /// double that recorded on the spot would report an event that the failed commit never
    /// published.
    /// </summary>
    internal static OutboxEventPublisher<IntegrationDbContext> Publisher(IntegrationDbContext db)
        => new(db);

    /// <summary>Events of type <typeparamref name="T"/> that actually reached the outbox.</summary>
    internal static IReadOnlyList<T> Published<T>(IntegrationDbContext db)
        => [.. db.OutboxMessages
            .AsEnumerable()
            .Where(m => m.EventType.StartsWith(typeof(T).FullName!, StringComparison.Ordinal))
            .Select(m => JsonSerializer.Deserialize<T>(m.PayloadJson, OutboxJson.Options)!)];

    internal static TenantInfo Tenant(Guid id) => new(
        Id: id,
        Name: "Test tenant",
        Fqdn: $"{id:N}.sankore.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// Emulates <c>ux_integration_kyc_limit_alert_period</c>, the unique index on
/// <c>(tenant_id, crm_customer_id, limit_kind, severity, period)</c>.
///
/// <para>
/// The InMemory provider enforces no index at all, so without this the dedup that INT-22's
/// criterion 2 is entirely made of would be untestable without a real PostgreSQL — and the branch
/// that catches the violation would never be exercised. It raises the exception Npgsql raises,
/// with the constraint name in the message, so the production code's recogniser is under test too.
/// </para>
///
/// <para>
/// One instance per logical database: the keys it has seen are its state, and every context
/// writing to that database must carry the same instance for the "index" to be shared the way a
/// real one is.
/// </para>
/// </summary>
internal sealed class LedgerUniqueIndexInterceptor : SaveChangesInterceptor
{
    private readonly HashSet<string> _keys = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null) return ValueTask.FromResult(result);

        foreach (var entry in context.ChangeTracker.Entries<KycLimitAlert>())
        {
            if (entry.State != EntityState.Added) continue;

            var alert = entry.Entity;
            var key = string.Join(
                '|',
                alert.TenantId, alert.CrmCustomerId, alert.LimitKind, alert.Severity, alert.Period);

            // The message is the one Npgsql produces for SQLSTATE 23505 — the index name is what
            // the production catch filter matches on, so a drift between the constant and the
            // migration shows up here as a dedup that stops working.
            if (!_keys.Add(key))
            {
                throw new DbUpdateException(
                    "23505: duplicate key value violates unique constraint "
                    + "\"ux_integration_kyc_limit_alert_period\"");
            }
        }

        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Keeps every log entry, so a test can assert that a skipped customer was reported rather than
/// dropped in silence.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));
}
