namespace Sankore.Shared.Infrastructure.Tests.BackgroundJobs;

using FluentAssertions;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Xunit;

/// <summary>
/// Scopes nest (sandbox lot 5): a job working in the test mode replays commands in another tenant
/// and must find its own scope again — the inner one used to clear everything on the way out.
/// </summary>
public sealed class BackgroundJobContextTests
{
    [Fact]
    public void Leaving_an_inner_scope_restores_the_outer_one_and_leaving_that_clears_it()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (BackgroundJobContext.SetScope(outer, Guid.NewGuid(), "job"))
        {
            using (BackgroundJobContext.SetScope(inner, Guid.NewGuid(), "replay"))
                BackgroundJobContext.CurrentTenant!.CurrentTenantId.Should().Be(inner);

            BackgroundJobContext.CurrentTenant!.CurrentTenantId.Should().Be(outer);
            BackgroundJobContext.CurrentUser!.DisplayName.Should().Be("job");
        }

        BackgroundJobContext.CurrentTenant.Should().BeNull();
        BackgroundJobContext.CurrentUser.Should().BeNull();
    }
}
