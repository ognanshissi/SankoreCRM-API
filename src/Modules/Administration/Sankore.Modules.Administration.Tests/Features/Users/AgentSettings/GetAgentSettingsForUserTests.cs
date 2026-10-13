namespace Sankore.Modules.Administration.Tests.Features.Users.AgentSettings;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.AgentSettings;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class GetAgentSettingsForUserTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public GetAgentSettingsForUserTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_administrator_reads_the_agent_settings_of_the_user_named_not_their_own()
    {
        Guid adminId, agentId;
        await using (var db = _factory.CreateContext())
        {
            var admin = AppUser.Create(_tenantId, Guid.NewGuid(), "Ambroise", "Bazie", "admin@imf.ci");
            var agent = AppUser.CreateAgent(_tenantId, Guid.NewGuid(), "Oumou", "Sidibe", "oumou@imf.ci", ["fr"], ["EPARGNE"]);
            var settings = Sankore.Modules.Administration.Domain.AgentSettings.Create(_tenantId, agent.Id);
            settings.SetCapacityCeiling(12);
            db.Users.AddRange(admin, agent);
            db.AgentSettings.Add(settings);
            await db.SaveChangesAsync();
            (adminId, agentId) = (admin.Id, agent.Id);
        }

        var current = Substitute.For<ICurrentUser>();
        current.Id.Returns(adminId);

        await using var read = _factory.CreateContext();
        var result = await new GetMyAgentSettingsHandler(read, current)
            .Handle(new GetMyAgentSettingsQuery(agentId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CapacityCeiling.Should().Be(12);
        result.Value.Products.Should().Equal("EPARGNE");
        result.Value.WorkingHoursConfigured.Should().BeFalse();
    }
}
