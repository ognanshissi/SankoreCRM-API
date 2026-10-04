namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Proves the dispatch-pool queries translate to PostgreSQL — which the rest of the suite cannot.
///
/// <para>
/// Every other test here runs on the EF InMemory provider, which evaluates almost any LINQ shape in
/// memory: a query it accepts can still be untranslatable by Npgsql, and then it fails for the first
/// time in production. This method's history is exactly that kind of failure — it threw
/// <c>NotImplementedException</c> and every dispatch answered 500 — so the replacement is checked
/// against the real provider rather than only against the double.
/// </para>
///
/// <para>
/// The specific trap here is <c>Contains</c>: on .NET 10 an array's <c>Contains</c> binds to the
/// <c>ReadOnlySpan&lt;T&gt;</c> extension and no longer translates, which is why both collections in
/// the facade are <c>List&lt;T&gt;</c>. Nothing in an InMemory run would notice.
/// </para>
///
/// <para>
/// No connection is opened: <c>ToQueryString</c> compiles the query and renders the SQL, which is
/// the step that throws when a shape cannot be translated.
/// </para>
/// </summary>
public sealed class GetAvailableAgentsSqlTranslationTests
{
    private static AdministrationDbContext NpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<AdministrationDbContext>()
            // Never connected to. The provider only has to be the real one.
            .UseNpgsql("Host=localhost;Database=unused;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AdministrationDbContext(options, new FixedTenantContext(Guid.NewGuid()));
    }

    /// <summary>
    /// The role-membership half of the facade, kept in step with it by hand.
    /// </summary>
    [Fact]
    public void The_role_membership_query_translates()
    {
        using var db = NpgsqlContext();
        var tenantId = Guid.NewGuid();
        var dispatchableRoles = new List<string> { Roles.CommercialAgent.Code, Roles.Agent.Code };

        var sql = db.UserRoles
            .IgnoreQueryFilters()
            .Where(ur => ur.TenantId == tenantId && ur.IsActive)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .Where(x => x.Name != null && dispatchableRoles.Contains(x.Name))
            .Select(x => x.UserId)
            .Distinct()
            .ToQueryString();

        sql.Should().Contain("user_roles").And.Contain("DISTINCT");
    }

    /// <summary>
    /// The agent half, with the agency filter applied — the branch a dispatched lead uses.
    /// </summary>
    [Fact]
    public void The_agent_query_translates_with_and_without_the_agency_filter()
    {
        using var db = NpgsqlContext();
        var tenantId = Guid.NewGuid();
        var agencyId = Guid.NewGuid();
        var agentIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var baseQuery = db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId
                     && agentIds.Contains(u.Id)
                     && u.IsAvailable
                     && u.Status == UserStatus.Active
                     && !u.IsSuperUser
                     && u.AccountType == UserAccountType.Standard
                     && u.AgencyId != null);

        var withoutAgency = baseQuery.OrderBy(u => u.Id).ToQueryString();
        var withAgency = baseQuery.Where(u => u.AgencyId == agencyId).OrderBy(u => u.Id).ToQueryString();

        withoutAgency.Should().Contain("ORDER BY");
        withAgency.Should().Contain("agency_id");
    }
}
