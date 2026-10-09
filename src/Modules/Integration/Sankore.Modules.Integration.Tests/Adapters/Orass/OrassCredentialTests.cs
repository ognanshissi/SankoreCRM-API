namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// ASS-06, criterion 3's « référence de secret » — WHERE the credential comes from, which is the
/// half of it that needs no specification.
///
/// <para>
/// ORASS is the first adapter in this module whose required credential depends on its carrier, so
/// these tests are mostly about the fork: the API carrier needs one vault entry, the bordereau
/// carrier needs two, and asking for the wrong set would refuse a perfectly configured connection.
/// </para>
/// </summary>
public sealed class OrassCredentialTests
{
    private static readonly Guid TenantId = OrassHarness.TenantId;
    private static readonly Guid ConnectionId = OrassHarness.ConnectionId;

    [Fact]
    public async Task The_api_carrier_asks_for_the_connection_credential_and_only_that()
    {
        var secrets = VaultHolding(OrassSecretNames.Credential);

        var probe = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.ExternalApi, default);

        probe.State.Should().Be(OrassCredentialState.Present);

        // The exact key, spelled out rather than computed with the adapter's own helper: a test
        // that used IntegrationSecrets would assert that the helper equals itself and stay green
        // through the one change that orphans every credential an IMF has already saved.
        await secrets.Received(1).GetHintAsync(
            Arg.Is<SecretKey>(k => k.TenantId == TenantId
                                   && k.Scope == OrassSecretNames.Scope
                                   && k.EntityId == ConnectionId
                                   && k.Name == OrassSecretNames.Credential),
            Arg.Any<CancellationToken>());

        // And nothing SFTP: demanding it would refuse an API-fed insurer for the absence of a
        // password it will never use, which is how an administrator learns to ignore the
        // activation screen.
        await secrets.DidNotReceive().GetHintAsync(
            Arg.Is<SecretKey>(k => k.Name == OrassSecretNames.SftpCredential),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_batch_carrier_asks_for_the_sftp_credential_AND_the_host_key_fingerprint()
    {
        var secrets = VaultHolding(
            OrassSecretNames.SftpCredential, OrassSecretNames.SftpHostKeyFingerprint);

        var probe = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.BatchSocle, default);

        probe.State.Should().Be(OrassCredentialState.Present);

        foreach (var name in new[]
        {
            OrassSecretNames.SftpCredential,
            OrassSecretNames.SftpHostKeyFingerprint,
        })
        {
            await secrets.Received(1).GetHintAsync(
                Arg.Is<SecretKey>(k => k.EntityId == ConnectionId && k.Name == name),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task A_batch_connection_with_a_password_and_no_fingerprint_is_still_missing_something()
    {
        var secrets = VaultHolding(OrassSecretNames.SftpCredential);

        var probe = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.BatchSocle, default);

        // The case worth having a test for, because an administrator does not expect it:
        // SftpFileTransport REFUSES TO CONNECT without the fingerprint rather than trusting
        // whatever key the server presents. Without this check the connection looks ready on the
        // activation screen and fails at the first cut-off, which is hours later and in a job log.
        probe.State.Should().Be(OrassCredentialState.Missing);
        probe.MissingDetail.Should().Contain("fingerprint");
        probe.MissingDetail.Should().Contain("ssh-keyscan");
    }

    [Fact]
    public async Task An_empty_vault_is_missing_whatever_the_carrier_needed()
    {
        var secrets = VaultHolding();

        var api = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.ExternalApi, default);

        var batch = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.BatchSocle, default);

        api.State.Should().Be(OrassCredentialState.Missing);
        batch.State.Should().Be(OrassCredentialState.Missing);

        // Different messages, because they send the administrator to save different things.
        api.MissingDetail.Should().NotBe(batch.MissingDetail);
    }

    [Fact]
    public async Task It_never_asks_the_vault_for_a_value()
    {
        var secrets = VaultHolding(
            OrassSecretNames.Credential,
            OrassSecretNames.SftpCredential,
            OrassSecretNames.SftpHostKeyFingerprint);

        await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.ExternalApi, default);

        await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.BatchSocle, default);

        // A decision, not a shortcut: nothing can be done with the value until the specification
        // says how it travels, so reading it would decrypt a secret into the memory of a process
        // that has no use for it — on a path a logger or an exception filter could later widen.
        await secrets.DidNotReceive().GetValueAsync(
            Arg.Any<SecretKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_entry_still_counts_as_present()
    {
        var secrets = Substitute.For<ISecretsModule>();

        secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SecretHint?>(
                new SecretHint(OrassSecretNames.Credential, "****", DateTimeOffset.UnixEpoch)));

        var probe = await OrassCredential.ProbeAsync(
            secrets, TenantId, ConnectionId, OrassCarrier.ExternalApi, default);

        // Deliberate alignment with the vault's actual behaviour: AesSecretsModule.GetValueAsync
        // hands out a stored value regardless of its expiry (recorded in the plan's §12 and
        // deliberately not fixed there), so calling an expired entry missing would report a state
        // the transport will not see — sending an administrator to re-enter a credential that still
        // authenticates. The day the vault enforces expiry, this test is the one that should fail.
        probe.State.Should().Be(OrassCredentialState.Present);
    }

    [Fact]
    public async Task It_refuses_a_null_vault_rather_than_reporting_a_missing_credential()
    {
        var act = async () => await OrassCredential.ProbeAsync(
            null!, TenantId, ConnectionId, OrassCarrier.ExternalApi, default);

        // A composition fault must not disguise itself as a configuration one: "no vault was
        // registered" and "this connection has no credential" send different people to different
        // places.
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void No_missing_credential_message_sends_the_operator_to_the_supplier()
    {
        foreach (var detail in new[]
        {
            OrassCredential.MissingApiDetail(),
            OrassCredential.MissingSftpCredentialDetail(),
            OrassCredential.MissingHostKeyDetail(),
        })
        {
            detail.Should().Contain("INT-03", "the fix is a secret endpoint the tenant already has");
            detail.Should().Contain("not the missing ORASS specification");
            detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
        }
    }

    /// <summary>
    /// A vault substitute holding a hint for exactly the named entries and nothing else — which is
    /// what makes "asked for the wrong key" a failing test rather than an invisible one.
    /// </summary>
    private static ISecretsModule VaultHolding(params string[] storedNames)
    {
        var secrets = Substitute.For<ISecretsModule>();

        secrets.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.Arg<SecretKey>();

                return Task.FromResult(
                    storedNames.Contains(key.Name, StringComparer.Ordinal)
                        ? new SecretHint(key.Name, "****", null)
                        : null);
            });

        return secrets;
    }
}
