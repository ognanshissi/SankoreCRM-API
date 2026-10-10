namespace Sankore.Modules.Integration.Tests.Features.Dispatch;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// The scaffolding the two dispatch job suites share: an InMemory context, a frozen clock, and a
/// command factory that only takes what a dispatch test cares about.
/// </summary>
internal static class DispatchTestContext
{
    internal static readonly DateTimeOffset Now = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    internal static IntegrationDbContext NewDb(Guid tenantId, string? name = null)
        => new(
            new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
                .Options,
            new FixedTenantContext(tenantId));

    /// <summary>
    /// A service provider shaped like the API's, for the jobs that create their own scope.
    ///
    /// <para>
    /// <c>ITenantContext</c> is registered exactly as <c>Program.cs</c> registers it — reading the
    /// ambient background context first — because that registration is the whole reason
    /// <c>BackgroundJobContext.SetScope</c> has to happen before <c>CreateScope</c>.
    /// </para>
    /// </summary>
    internal static ServiceProvider NewProvider(string databaseName, params Action<IServiceCollection>[] extra)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Now));

        services.AddScoped<ITenantContext>(_ =>
            BackgroundJobContext.CurrentTenant
            ?? throw new InvalidOperationException(
                "No ambient tenant: the job created its scope before establishing one."));

        services.AddDbContext<IntegrationDbContext>(
            opt => opt.UseInMemoryDatabase(databaseName), ServiceLifetime.Scoped);

        foreach (var configure in extra) configure(services);

        return services.BuildServiceProvider();
    }

    internal static IntegrationCommand Command(
        Guid tenantId,
        Guid crmId,
        DateTimeOffset createdAt,
        string entityType = IntegrationEntityTypes.Customer,
        CommandType type = CommandType.CreateCustomer,
        Guid? id = null,
        Guid? connectionId = null)
        => IntegrationCommand.Create(
            tenantId,
            connectionId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            type,
            entityType,
            crmId,
            new IdempotencyKey($"{type}:{crmId}:{createdAt.Ticks}:{id}"),
            createdBy: Guid.NewGuid(),
            clock: new FixedClock(createdAt),
            id: id);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
