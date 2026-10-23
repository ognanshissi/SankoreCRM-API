namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.GetClient;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// An Agent or a CommercialAgent consults only the clients assigned to them (product owner,
/// 2026-10-23): the ones they advise, and a client with no advisor yet when they created it. The
/// agency perimeter still applies on top.
/// </summary>
public sealed class ClientVisibilityTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser SignedIn(string role, params string[] permissions)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_me);
        user.TenantId.Returns(TenantId);
        user.IsAuthenticated.Returns(true);
        user.Roles.Returns([role]);
        user.Permissions.Returns(permissions);
        return user;
    }

    private async Task<Client> SeedAsync(string last, Guid? advisor, Guid createdBy, int n)
    {
        await using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(
            TenantId, AgencyId, last: last, clientNumber: $"ABJ-2026-00000{n}",
            advisorUserId: advisor, createdBy: createdBy);
        await TestClientFactory.SeedAsync(db, client);
        return client;
    }

    private async Task<IReadOnlyList<string>> SearchAsync(ICurrentUser user)
    {
        await using var db = _factory.CreateContext();
        var handler = new SearchClientsHandler(
            db, user, TestDoubles.AgencyScope(AgencyId), TestDoubles.Encryptor(), TestDoubles.Indexer(),
            visibility: new ClientVisibility(user));

        var result = await handler.Handle(
            new SearchClientsQuery(null, null, null, null, null, null, null, null, null, Page: 1, PageSize: 50),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return [.. result.Value!.Items.Select(c => c.ClientNumber).Order()];
    }

    [Fact]
    public async Task An_agent_finds_the_clients_they_advise_and_those_they_created_without_advisor()
    {
        await SeedAsync("Conseillé", advisor: _me, createdBy: _colleague, n: 1);
        await SeedAsync("Créé sans conseiller", advisor: null, createdBy: _me, n: 2);
        await SeedAsync("Créé puis confié", advisor: _colleague, createdBy: _me, n: 3);
        await SeedAsync("Chez un collègue", advisor: _colleague, createdBy: _colleague, n: 4);

        (await SearchAsync(SignedIn(Roles.Agent.Code)))
            .Should().Equal("ABJ-2026-000001", "ABJ-2026-000002");
    }

    [Fact]
    public async Task Customers_read_all_keeps_the_whole_perimeter()
    {
        await SeedAsync("Conseillé", advisor: _me, createdBy: _me, n: 1);
        await SeedAsync("Chez un collègue", advisor: _colleague, createdBy: _colleague, n: 2);

        (await SearchAsync(SignedIn(Roles.BranchManager.Code, Permissions.CanReadAllCustomers.Code)))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task A_read_by_id_of_a_colleagues_client_is_not_found()
    {
        var theirs = await SeedAsync("Chez un collègue", advisor: _colleague, createdBy: _colleague, n: 1);
        var mine = await SeedAsync("Conseillé", advisor: _me, createdBy: _colleague, n: 2);

        await using var db = _factory.CreateContext();
        var user = SignedIn(Roles.CommercialAgent.Code);
        var services = new ServiceCollection()
            .AddSingleton(user)
            .AddSingleton<CustomersDbContext>(db)
            .AddSingleton(new ClientVisibility(user))
            .BuildServiceProvider();
        var behavior = new ClientVisibilityBehavior<GetClientQuery, Result<string>>(services);

        var refused = await behavior.Handle(
            new GetClientQuery(theirs.Id), () => Task.FromResult(Result.Ok("reached")), default);
        var allowed = await behavior.Handle(
            new GetClientQuery(mine.Id), () => Task.FromResult(Result.Ok("reached")), default);

        refused.Error.Should().Be(CustomerErrors.ClientNotFound, "never a 403: that would confirm the client exists");
        allowed.Value.Should().Be("reached");
    }
}
