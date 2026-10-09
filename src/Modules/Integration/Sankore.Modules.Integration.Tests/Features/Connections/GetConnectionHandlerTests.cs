namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.GetConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class GetConnectionHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_detail_carries_the_settings_and_the_health_history()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, healthy: true);

        await using var db = _factory.CreateContext();
        var result = await new GetConnectionHandler(db)
            .Handle(new GetConnectionQuery(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Settings.Should().BeOfType<TemenosSettings>();
        result.Value.HasPassedHealthCheck.Should().BeTrue();
        result.Value.LastHealthStatus.Should().BeTrue();
        result.Value.LastHealthAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unknown_id_answers_not_found()
    {
        await using var db = _factory.CreateContext();

        var result = await new GetConnectionHandler(db)
            .Handle(new GetConnectionQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}
