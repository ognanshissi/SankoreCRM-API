namespace Sankore.Shared.Infrastructure.Tests.Auth;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// When the assigned-only restriction applies. Getting it wrong in the permissive direction hands an
/// agent every colleague's portfolio; in the other, it empties a supervisor's lists or a job's reads.
/// </summary>
public sealed class RecordVisibilityTests
{
    private static ICurrentUser User(string role, params string[] permissions)
    {
        var user = Substitute.For<ICurrentUser>();
        user.IsAuthenticated.Returns(true);
        user.Roles.Returns([role]);
        user.Permissions.Returns(permissions);
        return user;
    }

    [Fact]
    public void An_agent_without_the_permission_is_restricted()
        => RecordVisibility.SeesAll(User(Roles.Agent.Code), Permissions.CanReadAllLeads).Should().BeFalse();

    [Fact]
    public void The_permission_lifts_it_for_its_own_entity_only()
    {
        var user = User(Roles.Agent.Code, Permissions.CanReadAllLeads.Code);

        RecordVisibility.SeesAll(user, Permissions.CanReadAllLeads).Should().BeTrue();
        RecordVisibility.SeesAll(user, Permissions.CanReadAllCustomers).Should().BeFalse();
    }

    [Fact]
    public void Administrator_System_and_an_unauthenticated_context_are_never_restricted()
    {
        RecordVisibility.SeesAll(User(Roles.Administrator.Code), Permissions.CanReadAllKycFiles).Should().BeTrue();
        RecordVisibility.SeesAll(
            new SystemCurrentUser(Guid.NewGuid(), Guid.NewGuid(), "SYSTEM"), Permissions.CanReadAllKycFiles)
            .Should().BeTrue("every Hangfire job and scoped consumer runs as System");

        var anonymous = Substitute.For<ICurrentUser>();
        anonymous.IsAuthenticated.Returns(false);
        RecordVisibility.SeesAll(anonymous, Permissions.CanReadAllKycFiles).Should().BeTrue();
    }

    [Fact]
    public void The_http_user_reads_its_permissions_off_the_token()
    {
        var identity = new ClaimsIdentity(
            [new Claim("permission", "lead:read"), new Claim("permission", "lead:read:all")], "Bearer");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });

        new HttpContextCurrentUser(accessor).Permissions.Should().Equal("lead:read", "lead:read:all");
    }
}
