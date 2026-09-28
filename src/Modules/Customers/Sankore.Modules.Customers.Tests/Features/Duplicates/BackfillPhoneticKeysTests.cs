namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Xunit;

public sealed class BackfillPhoneticKeysTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public BackfillPhoneticKeysTests() => _factory = new TestCustomersDbContextFactory(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Job_fills_the_keys_of_the_clients_that_have_none()
    {
        await using var seed = _factory.CreateContext();
        var withoutKeys = await SeedWithoutKeysAsync(seed, "Awa", "Ouattara", "ABJ-2026-000001");
        var alreadyKeyed = await SeedWithoutKeysAsync(seed, "Mariam", "Traoré", "ABJ-2026-000002");

        var tracked = await seed.Clients.AsTracking().IgnoreQueryFilters().FirstAsync(c => c.Id == alreadyKeyed.Id);
        tracked.SetPhoneticKeys("PRESET", "PRESET2");
        await seed.SaveChangesAsync();
        seed.ChangeTracker.Clear();

        await using var provider = Provider();
        await new BackfillPhoneticKeysJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(_tenantId);

        await using var verify = _factory.CreateContext();
        var filled = verify.Clients.IgnoreQueryFilters().Single(c => c.Id == withoutKeys.Id);
        filled.PhoneticKeyPrimary.Should().Be(DuplicatesTestDoubles.PhoneticKeys.Compute("Ouattara"));
        filled.PhoneticKeySecondary.Should().Be(DuplicatesTestDoubles.PhoneticKeys.Compute("Awa"));

        // A client that already had keys is never touched: the job's predicate is "primary is null".
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == alreadyKeyed.Id)
            .PhoneticKeyPrimary.Should().Be("PRESET");
    }

    [Fact]
    public async Task Job_keys_a_legal_client_on_its_legal_name()
    {
        await using var seed = _factory.CreateContext();
        var legal = TestClientFactory.Legal(_tenantId, _agencyId, legalName: "Coopérative Djigui");
        await TestClientFactory.SeedAsync(seed, legal);

        await using var provider = Provider();
        await new BackfillPhoneticKeysJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(_tenantId);

        await using var verify = _factory.CreateContext();
        var stored = verify.Clients.IgnoreQueryFilters().Single(c => c.Id == legal.Id);
        stored.PhoneticKeyPrimary.Should().Be(DuplicatesTestDoubles.PhoneticKeys.Compute("Coopérative Djigui"));
        stored.PhoneticKeySecondary.Should().BeNull("a legal entity has a single name");
    }

    [Fact]
    public async Task Job_runs_under_the_SYSTEM_identity_and_leaves_other_tenants_alone()
    {
        var otherTenantId = Guid.NewGuid();

        await using var seed = _factory.CreateContext();
        var mine = await SeedWithoutKeysAsync(seed, "Awa", "Ouattara", "ABJ-2026-000001");
        var foreign = TestClientFactory.Individual(
            otherTenantId, _agencyId, first: "Awa", last: "Ouattara", clientNumber: "ABJ-2026-000009");
        await TestClientFactory.SeedAsync(seed, foreign);

        await using var provider = Provider();
        await new BackfillPhoneticKeysJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(_tenantId);

        await using var verify = _factory.CreateContext();
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == mine.Id)
            .PhoneticKeyPrimary.Should().NotBeNull();
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == foreign.Id)
            .PhoneticKeyPrimary.Should().BeNull("the job is scoped to one tenant by an explicit predicate");
    }

    [Fact]
    public async Task Job_terminates_even_when_a_name_yields_no_key()
    {
        await using var seed = _factory.CreateContext();
        // "123" normalizes to nothing a phonetic encoder can use: the client can never get a key,
        // and a naive loop re-selecting "primary is null" rows would spin forever.
        var unresolvable = await SeedWithoutKeysAsync(seed, "1", "234", "ABJ-2026-000001");

        await using var provider = Provider();
        var run = new BackfillPhoneticKeysJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(_tenantId);

        var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(run, "the job must not loop on a client it cannot key");

        await using var verify = _factory.CreateContext();
        verify.Clients.IgnoreQueryFilters().Single(c => c.Id == unresolvable.Id)
            .PhoneticKeyPrimary.Should().BeNull();
    }

    [Fact]
    public async Task Command_enqueues_the_job_for_the_callers_tenant()
    {
        var hangfire = Substitute.For<IBackgroundJobClient>();
        var currentUser = TestDoubles.CurrentUser(_tenantId, Guid.NewGuid());

        var result = await new BackfillPhoneticKeysHandler(hangfire, currentUser)
            .Handle(new BackfillPhoneticKeysCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        hangfire.ReceivedCalls().Should().HaveCount(1);
    }

    private async Task<Client> SeedWithoutKeysAsync(
        CustomersDbContext db, string first, string last, string clientNumber)
    {
        var client = TestClientFactory.Individual(
            _tenantId, _agencyId, first: first, last: last, clientNumber: clientNumber);

        client.PhoneticKeyPrimary.Should().BeNull(
            "the factory does not compute keys — that is exactly the gap this job fills");

        return await TestClientFactory.SeedAsync(db, client);
    }

    private ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton<IPhoneticKeyCalculator>(DuplicatesTestDoubles.PhoneticKeys);
        services.AddLogging();
        return services.BuildServiceProvider();
    }
}
