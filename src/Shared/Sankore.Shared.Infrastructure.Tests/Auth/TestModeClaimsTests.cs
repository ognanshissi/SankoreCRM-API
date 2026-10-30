namespace Sankore.Shared.Infrastructure.Tests.Auth;

using System.Security.Claims;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// Found in the sandbox's lot 7 run: the per-request session check read <c>tenant_id</c>, looked for
/// the session in the test twin — where it does not exist, sessions being the team's — and failed
/// every request of a test-mode session with a 401. The account's tenant is the team's.
/// </summary>
public sealed class TestModeClaimsTests
{
    private static readonly Guid Live = Guid.NewGuid();
    private static readonly Guid Twin = Guid.NewGuid();

    private static ClaimsPrincipal Token(params (string Type, Guid Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value.ToString())), "test"));

    [Fact]
    public void A_test_mode_token_belongs_to_the_team_tenant_not_the_twin()
        => TestModeClaims.AccountTenantOf(Token(("tenant_id", Twin), (TestModeClaims.TeamTenantId, Live))).Should().Be(Live);

    [Fact]
    public void An_ordinary_token_belongs_to_its_tenant()
        => TestModeClaims.AccountTenantOf(Token(("tenant_id", Live))).Should().Be(Live);

    [Fact]
    public void A_token_without_tenant_has_none()
        => TestModeClaims.AccountTenantOf(Token()).Should().BeNull();
}
