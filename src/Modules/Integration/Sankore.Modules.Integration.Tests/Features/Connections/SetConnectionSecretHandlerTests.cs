namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class SetConnectionSecretHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly ISecretsModule _secrets = Substitute.For<ISecretsModule>();

    public void Dispose() => _factory.Dispose();

    private SetConnectionSecretHandler Build(Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db)
        => new(db, _secrets, ConnectionsTestHarness.User(Tenant),
            ConnectionsTestHarness.Log<SetConnectionSecretHandler>());

    [Fact]
    public async Task The_value_goes_to_the_vault_under_the_connections_own_key()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new SetConnectionSecretCommand(connection.Id, "connection-credential", "s3cr3t"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Keyed per CONNECTION, not per tenant: an IMF holds a CBS and two insurers at once, and
        // a tenant-wide key would make saving the second destroy the first.
        await _secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k =>
                k.TenantId == Tenant
                && k.Scope == "integration"
                && k.EntityId == connection.Id
                && k.Name == "connection-credential"),
            "s3cr3t",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_slot_has_its_own_key_so_rotating_one_does_not_destroy_the_others()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(
            seed, Tenant, kind: IntegrationKind.Amplitude, mode: IntegrationMode.Batch);

        await using var db = _factory.CreateContext();
        var handler = Build(db);

        foreach (var name in new[] { "connection-credential", "sftp-credential", "webhook-secret" })
        {
            (await handler.Handle(
                new SetConnectionSecretCommand(connection.Id, name, $"value-of-{name}"),
                CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await _secrets.Received(3).SetAsync(
            Arg.Any<SecretKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());

        await _secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k => k.Name == "sftp-credential"),
            "value-of-sftp-credential", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_value_is_trimmed_before_it_reaches_the_vault()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        await Build(db).Handle(
            new SetConnectionSecretCommand(connection.Id, "connection-credential", "  token\n"),
            CancellationToken.None);

        // A credential pasted from a console carries a trailing newline, and it would travel into
        // an Authorization header as-is: a 401 whose cause is invisible on both sides.
        await _secrets.Received(1).SetAsync(
            Arg.Any<SecretKey>(), "token", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expiry_is_carried_through()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);
        var expires = ConnectionsTestHarness.Now.AddDays(90);

        await using var db = _factory.CreateContext();
        await Build(db).Handle(
            new SetConnectionSecretCommand(connection.Id, "connection-credential", "k", expires),
            CancellationToken.None);

        await _secrets.Received(1).SetAsync(
            Arg.Any<SecretKey>(), "k", expires, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found_and_writes_nothing()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new SetConnectionSecretCommand(Guid.NewGuid(), "connection-credential", "k"),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        // Without the existence check a caller could seed vault entries under any id they cared
        // to guess, and the orphans would outlive every connection anyone could see.
        await _secrets.DidNotReceive().SetAsync(
            Arg.Any<SecretKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_slot_name_writes_nothing()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();

        // The validator refuses this in production; the handler's own guard makes sure an
        // unmapped name never reports success after writing nowhere.
        var result = await Build(db).Handle(
            new SetConnectionSecretCommand(connection.Id, "root-password", "k"),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.SettingsInvalid);

        await _secrets.DidNotReceive().SetAsync(
            Arg.Any<SecretKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }
}
