namespace Sankore.Modules.Integration.Tests.Infrastructure.CallLog;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The append itself: a row, on its own unit of work, in a scope of its own.
///
/// <para>
/// What this suite can and cannot prove is worth stating. The EF InMemory provider ignores
/// <see cref="System.Transactions.TransactionScope"/> entirely, so "the row survives the
/// caller's rollback" is not testable here — it belongs to the suppressed scope in
/// <see cref="CallLogStore"/> and to PostgreSQL. What IS testable, and what actually broke in
/// M01's equivalent, is the other half: the append must not ride on the caller's context, or it
/// flushes that handler's half-built aggregate at a point the handler never chose.
/// </para>
/// </summary>
public sealed class CallLogStoreTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid Connection = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Appends_a_row_that_another_context_can_read_back()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var provider = BuildProvider(factory);

        var store = new CallLogStore(provider.GetRequiredService<IServiceScopeFactory>());

        await store.AppendAsync(Row(Tenant, "CreateCustomer"), CancellationToken.None);

        await using var reader = factory.CreateContext();
        var rows = await reader.CallLogs.ToListAsync();

        rows.Should().ContainSingle().Which.Operation.Should().Be("CreateCustomer");
    }

    [Fact]
    public async Task Does_not_flush_the_callers_pending_changes()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var provider = BuildProvider(factory);

        // Stands in for the handler's own context, mid-transaction, with something half-built in
        // its change tracker.
        await using var callerContext = factory.CreateContext();
        callerContext.CallLogs.Add(Row(Tenant, "NotYetCommitted"));

        var store = new CallLogStore(provider.GetRequiredService<IServiceScopeFactory>());
        await store.AppendAsync(Row(Tenant, "Journalled"), CancellationToken.None);

        await using var reader = factory.CreateContext();
        var operations = await reader.CallLogs.Select(l => l.Operation).ToListAsync();

        operations.Should().BeEquivalentTo(
            ["Journalled"],
            "the journal commits its own row and leaves the caller's unit of work untouched");
    }

    [Fact]
    public async Task A_row_is_visible_only_to_the_tenant_that_made_the_call()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        var provider = BuildProvider(factory);

        var store = new CallLogStore(provider.GetRequiredService<IServiceScopeFactory>());
        await store.AppendAsync(Row(Tenant, "CreateCustomer"), CancellationToken.None);

        await using var otherTenantReader = factory.ContextFor(OtherTenant);

        (await otherTenantReader.CallLogs.ToListAsync()).Should().BeEmpty(
            "the row carries the tenant the adapter was called for, and the context's query "
            + "filter is what keeps one tenant's journal out of another's screen");
    }

    /// <summary>
    /// A container whose <c>IntegrationDbContext</c> is the InMemory one of the factory, so the
    /// child scope the store creates resolves a real context over the same database. Registered
    /// scoped, as the module registers it.
    /// </summary>
    private static ServiceProvider BuildProvider(TestIntegrationDbContextFactory factory)
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => factory.CreateContext());
        services.AddSingleton<ITenantContext>(new FixedTenantContext(factory.TenantId));

        return services.BuildServiceProvider();
    }

    private static IntegrationCallLog Row(Guid tenantId, string operation) =>
        IntegrationCallLog.Record(
            tenantId: tenantId,
            connectionId: Connection,
            operation: operation,
            durationMs: 12,
            at: DateTimeOffset.Parse("2026-03-04T10:00:00Z", CultureInfo.InvariantCulture),
            errorFamily: ErrorFamily.Transient,
            errorCode: IntegrationErrors.Timeout,
            correlationId: "corr-1");
}
