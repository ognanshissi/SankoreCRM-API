namespace Sankore.Modules.Customers.Tests.Infrastructure;

using System.Reflection;
using FluentAssertions;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// Composition-root tests. Every other test in this project builds a handler with `new`,
/// which proves the handler's logic but says nothing about whether the container can
/// actually produce it. With 10 vertical-slice folders each registering its own services,
/// a forgotten `AddScoped` surfaces only when a request hits the endpoint in production —
/// so these tests resolve every handler, consumer and background job the module ships.
///
/// No database is touched: `AddDbContext` only records how to build a connection.
/// </summary>
public sealed class CustomersModuleCompositionTests
{
    private static readonly Assembly ModuleAssembly = typeof(CustomersModule).Assembly;

    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Host=localhost;Database=composition-test;Username=x;Password=y",
                // 32-byte base64 keys: never used to encrypt here, only to satisfy construction.
                ["Customers:FieldEncryptionKey"] = Convert.ToBase64String(new byte[32]),
                ["Customers:BlindIndexKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);

        // Dependencies the host owns, not the module. Substituted so a missing module-side
        // registration is the only thing this test can fail on.
        services.AddScoped(_ => Substitute.For<ICurrentUser>());
        services.AddScoped<ITenantContext>(_ => new FixedTenantContext(Guid.NewGuid()));
        services.AddScoped(_ => Substitute.For<IAgencyScopeProvider>());
        services.AddScoped(_ => Substitute.For<IAdministrationModule>());
        services.AddScoped(_ => Substitute.For<INotificationsModule>());
        services.AddScoped(_ => Substitute.For<IWorkflowModule>());
        services.AddScoped(_ => Substitute.For<IKycModule>());
        services.AddScoped(_ => Substitute.For<ILeadsModule>());
        // Singleton in production (CachedTenantStore), so keep the lifetime honest here:
        // a job resolved from the root container must be able to depend on it.
        services.AddSingleton(_ => Substitute.For<ITenantStore>());
        services.AddSingleton(_ => Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>());
        services.AddSingleton(_ => Substitute.For<Hangfire.IBackgroundJobClient>());
        services.AddSingleton(_ => Substitute.For<IBus>());

        services.AddCustomersModule(config);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Catches a singleton capturing a scoped service — the classic way a DbContext
            // silently outlives its request and starts returning stale or cross-tenant data.
            ValidateScopes = true,
        });
    }

    /// <summary>Closed <c>IRequestHandler&lt;,&gt;</c> interfaces implemented in the module.</summary>
    private static IEnumerable<Type> HandlerInterfaces() =>
        ModuleAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .Distinct();

    private static IEnumerable<Type> ConsumerTypes() =>
        ModuleAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                        && t.GetInterfaces().Any(i =>
                            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>)));

    /// <summary>
    /// Hangfire job classes: named <c>*Job</c>, living under <c>Features/</c>, and exposing the
    /// <c>ExecuteAsync</c> entry point Hangfire invokes. The name alone is not enough — the
    /// domain also has a <c>ClientExportJob</c> entity, which is a row, not a job.
    /// </summary>
    private static IEnumerable<Type> JobTypes() =>
        ModuleAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && t.Name.EndsWith("Job", StringComparison.Ordinal)
                        && t.Namespace?.Contains(".Features.", StringComparison.Ordinal) == true
                        && t.GetMethod("ExecuteAsync") is not null);

    [Fact]
    public void The_container_builds_and_the_dbcontext_resolves()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<Customers.Infrastructure.CustomersDbContext>()
            .Should().NotBeNull();
    }

    [Fact]
    public void Every_mediatr_handler_in_the_module_can_be_resolved()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var unresolvable = new List<string>();
        foreach (var handlerInterface in HandlerInterfaces())
        {
            try
            {
                if (scope.ServiceProvider.GetService(handlerInterface) is null)
                    unresolvable.Add($"{handlerInterface.Name}: not registered");
            }
            catch (InvalidOperationException ex)
            {
                unresolvable.Add($"{handlerInterface.Name}: {ex.Message}");
            }
        }

        unresolvable.Should().BeEmpty(
            "every command and query the module ships must be dispatchable through MediatR");
    }

    [Fact]
    public void Every_message_consumer_can_be_resolved()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var unresolvable = ConsumerTypes()
            .Where(t => ActivatorUtilities.CreateInstance(scope.ServiceProvider, t) is null)
            .Select(t => t.Name)
            .ToList();

        unresolvable.Should().BeEmpty();
    }

    [Fact]
    public void Every_background_job_can_be_resolved()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var unresolvable = new List<string>();
        foreach (var jobType in JobTypes())
        {
            try
            {
                // Hangfire activates jobs from the root container, so a job must not depend
                // on a scoped service: it takes IServiceScopeFactory and opens its own scope.
                _ = provider.GetService(jobType) ?? ActivatorUtilities.CreateInstance(provider, jobType);
            }
            catch (Exception ex)
            {
                unresolvable.Add($"{jobType.Name}: {ex.Message}");
            }
        }

        unresolvable.Should().BeEmpty(
            "a recurring job that cannot be activated from the root container fails silently at run time");
    }

    [Fact]
    public void The_cross_module_contracts_resolve_to_this_modules_implementations()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<ICustomersModule>().Should().NotBeNull();

        // The legacy Customer360 contract Leads already calls must now be served from real
        // client records, not from Sankore.Api.Stubs.StubCustomerModule.
        var legacy = scope.ServiceProvider.GetService<Modules.Customer360.PublicApi.ICustomerModule>();
        legacy.Should().NotBeNull();
        legacy!.GetType().Name.Should().Be("LegacyCustomerModuleAdapter");
    }
}
