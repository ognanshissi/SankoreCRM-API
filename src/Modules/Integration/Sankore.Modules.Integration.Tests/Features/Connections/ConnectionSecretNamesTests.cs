namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Connections;
using Xunit;

/// <summary>
/// The name → vault key mapping both secret slices run on. The status endpoint has no handler of
/// its own — it reads the vault's hints directly, because there is no decision to take and
/// nothing to audit about asking whether a setting exists — so this is where its logic is pinned.
/// </summary>
public sealed class ConnectionSecretNamesTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Connection = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("connection-credential")]
    [InlineData("sftp-credential")]
    [InlineData("webhook-secret")]
    public void Every_declared_slot_maps_to_a_key_scoped_to_the_tenant_and_the_connection(string name)
    {
        ConnectionSecretNames.IsKnown(name).Should().BeTrue();

        var key = ConnectionSecretNames.KeyFor(name, Tenant, Connection);

        key.Should().NotBeNull();
        key!.TenantId.Should().Be(Tenant);
        key.EntityId.Should().Be(Connection);
        key.Scope.Should().Be("integration");
        key.Name.Should().Be(name);
    }

    [Fact]
    public void The_three_slots_never_share_a_key()
    {
        var keys = ConnectionSecretNames.All
            .Select(n => ConnectionSecretNames.KeyFor(n, Tenant, Connection))
            .ToList();

        // Switching a connection from SMTP-style credentials to an API key, or rotating the SFTP
        // password, must not destroy a neighbour.
        keys.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("root-password")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_name_is_refused_rather_than_silently_creating_a_fourth_slot(string? name)
    {
        ConnectionSecretNames.IsKnown(name).Should().BeFalse();
        ConnectionSecretNames.KeyFor(name, Tenant, Connection).Should().BeNull();
    }

    [Fact]
    public void Two_connections_of_the_same_tenant_hold_separate_credentials()
    {
        var other = Guid.Parse("33333333-3333-3333-3333-333333333333");

        ConnectionSecretNames.KeyFor("connection-credential", Tenant, Connection)
            .Should().NotBe(ConnectionSecretNames.KeyFor("connection-credential", Tenant, other));
    }
}
