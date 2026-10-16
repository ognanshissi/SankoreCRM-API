namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// <c>IAdministrationModule.GetUserAgencyAsync</c> (TEL-B-19).
///
/// <para>
/// It exists next to <c>GetAgentAsync</c> for one reason and this suite is about that reason: its
/// callers run OUTSIDE a request. The telephony webhook route is deliberately exempt from host
/// resolution, so it derives its tenant from the connection and carries it by hand — and
/// <c>GetAgentAsync</c>, which leans on the ambient filter, would answer nothing there, or
/// somebody else's user. What is pinned here is that the tenant in the argument is the tenant
/// that answers.
/// </para>
/// </summary>
public sealed class GetUserAgencyTests : IDisposable
{
    private static readonly Guid Branch = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public GetUserAgencyTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private AdministrationModuleFacade Facade()
        => new(_factory.CreateContext(), new ProductCatalogCache());

    [Fact]
    public async Task An_agents_agency_is_answered()
    {
        var agent = Seed("Awa", Branch, _tenantId);

        (await Facade().GetUserAgencyAsync(_tenantId, agent.Id, default)).Should().Be(Branch);
    }

    [Fact]
    public async Task A_user_with_no_agency_answers_null_and_never_Guid_Empty()
    {
        var root = Seed("Racine", agencyId: null, tenantId: _tenantId);

        // M12 permits it and documents it as ordinary for a head-office account. The caller's
        // contract says null means "outside every restricted perimeter", never "inside all of
        // them" — and Guid.Empty leaking out would be read as an agency by a comparison.
        var agency = await Facade().GetUserAgencyAsync(_tenantId, root.Id, default);

        agency.Should().BeNull();
    }

    [Fact]
    public async Task A_user_of_ANOTHER_tenant_answers_null()
    {
        var other = Guid.NewGuid();
        var stranger = Seed("Etranger", Branch, other);

        // The whole point of the explicit tenant. Answering their agency here would stamp one
        // tenant's branch onto another tenant's call.
        (await Facade().GetUserAgencyAsync(_tenantId, stranger.Id, default)).Should().BeNull();

        // And it IS reachable under its own tenant, so the refusal above is the predicate and not
        // a seeding accident.
        (await Facade().GetUserAgencyAsync(other, stranger.Id, default)).Should().Be(Branch);
    }

    [Fact]
    public async Task An_unknown_user_answers_null_rather_than_throwing()
    {
        (await Facade().GetUserAgencyAsync(_tenantId, Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task Empty_arguments_answer_null_without_a_query()
    {
        (await Facade().GetUserAgencyAsync(Guid.Empty, Guid.NewGuid(), default)).Should().BeNull();
        (await Facade().GetUserAgencyAsync(_tenantId, Guid.Empty, default)).Should().BeNull();
    }

    private AppUser Seed(string name, Guid? agencyId, Guid tenantId)
    {
        var user = agencyId is { } agency
            ? AppUser.CreateAgent(
                tenantId, agency, name, "Agent", $"{name}@example.test", ["fr"], ["Loan"])
            : AppUser.CreateRoot(tenantId, name, "Root", $"{name}@example.test");

        user.Activate();

        using var db = _factory.CreateContext();
        db.Users.Add(user);
        db.SaveChanges();

        return user;
    }
}
