namespace Sankore.Modules.Integration.Tests.Infrastructure.Transport;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// What the direct transport refuses BEFORE it opens anything, and what it tells the caller when
/// it does.
///
/// <para>
/// No SFTP server is stood up here, and none is needed: every case below is decided before a
/// socket exists. "No connection was attempted" is asserted through the vault — the egress check
/// runs before the credential is fetched, so a refused target leaves
/// <c>ISecretsModule</c> untouched and nothing about the connection has left the process.
/// </para>
/// </summary>
public sealed class SftpFileTransportRefusalTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly byte[] Content = [1, 2, 3, 4];

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("localhost")]
    public async Task An_internal_target_is_refused_without_a_connection_being_attempted(string host)
    {
        var secrets = Substitute.For<ISecretsModule>();

        var transport = Transport(secrets, allowPrivate: false);

        var result = await transport.PutAsync(
            Connection(host), "20260311-000000001.csv", Content, CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        // Nothing about this connection left the process: the credential was never fetched, so no
        // socket can have been opened and no authentication attempted.
        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    [Fact]
    public async Task Unreachable_timed_out_and_host_key_refused_are_one_message_to_the_caller()
    {
        // Three distinguishable answers would turn the deposit into a scanner that maps SANKORE's
        // internal network one address at a time — which is the SSRF's real prize once connecting
        // is blocked. So the blocked-address refusal and the name-does-not-resolve refusal must be
        // literally the same result; the distinguishing detail is in the server log.
        var blocked = await Transport(Substitute.For<ISecretsModule>(), allowPrivate: false)
            .PutAsync(Connection("10.0.0.5"), "f.csv", Content, CancellationToken.None);

        var unresolvable = await Transport(Substitute.For<ISecretsModule>(), allowPrivate: false)
            .PutAsync(
                Connection("sftp.sankore-does-not-exist.invalid"), "f.csv", Content,
                CancellationToken.None);

        blocked.Code.Should().Be(unresolvable.Code);
        blocked.Family.Should().Be(unresolvable.Family);
        blocked.Detail.Should().Be(unresolvable.Detail);

        // And the family is collapsed with the message: leaving one Technical and the other
        // Transient would rebuild the same oracle out of last_error_family.
        blocked.Family.Should().Be(ErrorFamily.Transient);
        blocked.Code.Should().Be(IntegrationErrors.Unavailable);

        // The message says nothing about the target.
        blocked.Detail.Should().NotContain("10.0.0.5").And.NotContain("sankore-does-not-exist");
    }

    [Fact]
    public async Task With_no_host_key_fingerprint_stored_it_refuses_to_connect()
    {
        var secrets = Substitute.For<ISecretsModule>();

        // A credential IS stored; only the fingerprint is missing.
        secrets.GetValueAsync(
                Arg.Is<SecretKey>(k => k.Name == IntegrationSecrets.SftpCredentialName),
                Arg.Any<CancellationToken>())
            .Returns("a-password");

        secrets.GetValueAsync(
                Arg.Is<SecretKey>(k => k.Name == IntegrationSecrets.SftpHostKeyFingerprintName),
                Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var stopwatch = Stopwatch.StartNew();

        var result = await Transport(secrets, allowPrivate: false)
            .PutAsync(Connection("8.8.8.8"), "f.csv", Content, CancellationToken.None);

        stopwatch.Stop();

        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(
            ErrorFamily.Technical, "an administrator has to store the fingerprint; no retry helps");
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);

        // The message has to say what to do, because this is a refusal the administrator resolves.
        result.Detail.Should().Contain(IntegrationSecrets.SftpHostKeyFingerprintName);
        result.Detail.Should().Contain("Refusing to connect");
        result.Detail.Should().Contain("ssh-keyscan");

        // It returned before any socket existed. The configured budget is 60 seconds, so a
        // connection attempt to 8.8.8.8:22 could not have completed or timed out inside this.
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(5), "the refusal happens before an SftpClient is constructed");
    }

    [Fact]
    public async Task A_missing_credential_is_reported_distinguishably_because_it_reveals_no_network()
    {
        // Kept distinguishable on purpose: it describes configuration the administrator wrote and
        // says nothing about what is reachable. Only the network-decided outcomes are collapsed.
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await Transport(secrets, allowPrivate: false)
            .PutAsync(Connection("8.8.8.8"), "f.csv", Content, CancellationToken.None);

        result.Code.Should().Be(IntegrationErrors.CredentialMissing);
        result.Detail.Should().Contain(IntegrationSecrets.SftpCredentialName);
    }

    [Fact]
    public async Task A_relay_routed_connection_is_refused_by_the_direct_transport()
    {
        // Defence in depth. The router never sends a relay connection here; if something bypassed
        // it, the egress guard would be asked to validate an address that is SUPPOSED to be
        // private — so the direct transport refuses rather than resolving.
        var secrets = Substitute.For<ISecretsModule>();

        var relayRouted = Connection("10.0.0.5", relayAgentId: Guid.NewGuid());

        var result = await Transport(secrets, allowPrivate: true)
            .PutAsync(relayRouted, "f.csv", Content, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Detail.Should().Contain("relay");

        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    [Fact]
    public async Task A_connection_whose_settings_are_not_batch_capable_is_refused()
    {
        var secrets = Substitute.For<ISecretsModule>();

        var apiOnly = Sankore.Modules.Integration.Tests.Features.Commands
            .CommandsTestHarness.Connection(Tenant, mode: IntegrationMode.Api, id: ConnectionId);

        var result = await Transport(secrets, allowPrivate: false)
            .PutAsync(apiOnly, "f.csv", Content, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b.csv")]
    [InlineData("a\\b.csv")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task A_file_name_that_is_not_one_safe_segment_is_refused(string fileName)
    {
        var secrets = Substitute.For<ISecretsModule>();

        var result = await Transport(secrets, allowPrivate: false)
            .PutAsync(Connection("8.8.8.8"), fileName, Content, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Code.Should().Be(IntegrationErrors.SettingsInvalid);

        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    private static SftpFileTransport Transport(ISecretsModule secrets, bool allowPrivate)
        => new(
            secrets,
            new SftpEgressGuard(
                Options.Create(new IntegrationEgressOptions { AllowPrivateAddresses = allowPrivate }),
                NullLogger<SftpEgressGuard>.Instance),
            NullLogger<SftpFileTransport>.Instance);

    private static IntegrationConnection Connection(string host, Guid? relayAgentId = null)
    {
        var now = new DateTimeOffset(2026, 3, 11, 18, 5, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);

        return IntegrationConnection.Create(
            tenantId: Tenant,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.PerfectVision,
            mode: relayAgentId is null ? IntegrationMode.Batch : IntegrationMode.Relay,
            name: "Perfect Vision batch",
            settings: new PerfectVisionSettings
            {
                CutOffTime = new TimeOnly(18, 0),
                OutboundDirectory = "/upload/sankore",
                InboundDirectory = "/download/sankore",
                SftpHost = host,
                SftpPort = 22,
                SftpUsername = "sankore",
                TimeoutSeconds = 60,
            },
            createdBy: Guid.NewGuid(),
            clock: clock,
            relayAgentId: relayAgentId,
            id: ConnectionId);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
