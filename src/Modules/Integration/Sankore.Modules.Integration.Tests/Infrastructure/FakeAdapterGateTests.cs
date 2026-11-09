namespace Sankore.Modules.Integration.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The in-memory adapter on every platform, for test institutions only (owner, 2026-11-08 — demos
/// on the production platform). A real institution must never get its invented successes: refused
/// at every resolution, even holding an active Fake connection from before its test flag was lifted.
/// </summary>
public sealed class FakeAdapterGateTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _db;
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();

    public FakeAdapterGateTests() => _db = new TestIntegrationDbContextFactory(_tenantId);

    public void Dispose() => _db.Dispose();

    private void Tenant(bool isTest)
        => _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Démo", "demo.local", true, false, null, null, IsTest: isTest));

    private FakeAdapterGate Gate(StubHostEnvironment env) => new(env, _tenants);

    [Fact]
    public async Task Development_always_and_production_only_for_a_test_institution()
    {
        Tenant(isTest: false);
        (await Gate(StubHostEnvironment.Development).AllowsAsync(_tenantId, default)).Should().BeTrue();
        (await Gate(StubHostEnvironment.Production).AllowsAsync(_tenantId, default)).Should().BeFalse();

        Tenant(isTest: true);
        (await Gate(StubHostEnvironment.Production).AllowsAsync(_tenantId, default)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_institution_reads_as_real()
        => (await Gate(StubHostEnvironment.Production).AllowsAsync(Guid.NewGuid(), default)).Should().BeFalse();

    [Fact]
    public async Task A_real_institution_keeping_an_active_fake_connection_resolves_nothing()
    {
        var connection = IntegrationConnection.Create(_tenantId, IntegrationFamily.Insurance, IntegrationKind.Fake,
            IntegrationMode.Api, "Assureur de démonstration", new FakeSettings { Family = IntegrationFamily.Insurance },
            Guid.NewGuid(), TimeProvider.System);
        connection.RecordHealth(new IntegrationHealth(true, null, null, DateTimeOffset.UtcNow), TimeProvider.System);
        connection.Activate(Guid.NewGuid(), TimeProvider.System).IsSuccess.Should().BeTrue();
        await using (var db = _db.CreateContext())
        {
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
        }
        var resolver = new IntegrationAdapterResolver(_db.CreateContext(), new ServiceCollection().BuildServiceProvider(),
            Gate(StubHostEnvironment.Production));

        Tenant(isTest: true);
        (await resolver.ResolveConnectionAsync(_tenantId, connection.Id, default)).IsSuccess.Should().BeTrue();

        Tenant(isTest: false);
        var refused = await resolver.ResolveConnectionAsync(_tenantId, connection.Id, default);
        refused.IsSuccess.Should().BeFalse();
        refused.Code.Should().Be(IntegrationErrors.FakeAdapterNotAllowed);
        (await resolver.ResolveConnectionAsync(_tenantId, IntegrationFamily.Insurance, default)).Code
            .Should().Be(IntegrationErrors.FakeAdapterNotAllowed);
    }

    [Fact]
    public async Task Without_a_gate_the_double_is_refused_fail_closed()
    {
        var connection = IntegrationConnection.Create(_tenantId, IntegrationFamily.Insurance, IntegrationKind.Fake,
            IntegrationMode.Api, "Assureur de démonstration", new FakeSettings { Family = IntegrationFamily.Insurance },
            Guid.NewGuid(), TimeProvider.System);
        await using (var db = _db.CreateContext())
        {
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
        }

        var resolver = new IntegrationAdapterResolver(_db.CreateContext(), new ServiceCollection().BuildServiceProvider());

        (await resolver.ResolveConnectionAsync(_tenantId, connection.Id, default)).Code
            .Should().Be(IntegrationErrors.FakeAdapterNotAllowed);
    }
}
