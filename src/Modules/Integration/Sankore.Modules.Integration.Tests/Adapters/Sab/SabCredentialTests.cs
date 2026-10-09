namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-32, criterion 1 — the half that does not need the catalogue: « authentifiée par clé API ».
///
/// <para>
/// What is verifiable today is WHERE the key comes from and WHAT is done with it, which is most of
/// what can go wrong about a credential. How it travels on the wire is the catalogue's business
/// (question 2) and is deliberately absent from the code these tests cover.
/// </para>
/// </summary>
public sealed class SabCredentialTests
{
    private static readonly Guid TenantId = new("66666666-6666-6666-6666-666666666666");

    private static readonly Guid ConnectionId = new("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task A_stored_key_is_reported_present_and_an_absent_one_missing()
    {
        var withKey = VaultHolding(new SecretHint(IntegrationSecretNames.Credential, "****", null));
        var empty = VaultHolding(null);

        (await SabCredential.ProbeAsync(withKey, TenantId, ConnectionId, default))
            .Should().Be(SabCredentialState.Present);

        (await SabCredential.ProbeAsync(empty, TenantId, ConnectionId, default))
            .Should().Be(SabCredentialState.Missing);
    }

    [Fact]
    public async Task The_key_is_looked_up_per_connection_and_under_the_shared_integration_scope()
    {
        var vault = VaultHolding(new SecretHint(IntegrationSecretNames.Credential, "****", null));

        await SabCredential.ProbeAsync(vault, TenantId, ConnectionId, default);

        // Spelled out rather than built with the module's own helper, because a test that computed
        // the key the way the adapter does would assert that the helper equals itself. The failure
        // modes this pins are both silent: a lookup keyed per TENANT would make this adapter read
        // the credential of whichever connection was saved last — an IMF has a core banking system
        // and two insurers at once — and a different scope or name would orphan every key an IMF
        // has already saved, reporting a configured connection as unconfigured.
        await vault.Received(1).GetHintAsync(
            Arg.Is<SecretKey>(k => k.TenantId == TenantId
                                   && k.Scope == IntegrationSecretNames.Scope
                                   && k.EntityId == ConnectionId
                                   && k.Name == IntegrationSecretNames.Credential),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_secret_value_is_never_read()
    {
        var vault = VaultHolding(new SecretHint(IntegrationSecretNames.Credential, "****", null));

        await SabCredential.ProbeAsync(vault, TenantId, ConnectionId, default);

        // The point of the hint. Nothing can be done with an Open SAB API key until the catalogue
        // says how it travels, so a value read here would be a secret decrypted into the memory of
        // a process that has no use for it — on a path a logger, an exception filter or a future
        // "while we are here" refactor could widen. The day the transport exists, the value is
        // read there and nowhere else.
        await vault.DidNotReceive().GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_entry_still_counts_as_present()
    {
        var expired = VaultHolding(
            new SecretHint(IntegrationSecretNames.Credential, "****", DateTimeOffset.UnixEpoch));

        // ISecretsModule.GetValueAsync hands out a stored value regardless of its ExpiresAt, so a
        // probe that called this missing would report a state the transport will never see — and
        // send an administrator to re-enter a key that still authenticates. Expiry is the vault's
        // business, in one place, for every module. (That GetValueAsync ignores expiry at all is
        // noted as a finding about the vault, not worked around here.)
        (await SabCredential.ProbeAsync(expired, TenantId, ConnectionId, default))
            .Should().Be(SabCredentialState.Present);
    }

    [Fact]
    public async Task It_refuses_to_probe_without_a_vault()
    {
        var act = async () => await SabCredential.ProbeAsync(null!, TenantId, ConnectionId, default);

        // At composition, not at the first command: a host that wires the adapter without
        // AddSecretsVault must learn it loudly rather than through a rejected customer creation in
        // a job log hours later.
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void The_refusal_message_sends_the_reader_to_the_secret_endpoint_and_not_to_SBS()
    {
        var detail = SabCredential.MissingDetail();

        // "No API key is configured for this connection" is a mistake the tenant's own
        // administrator fixes in a minute; "no Open SAB catalogue" is a procurement conversation.
        // Collapsing the two sends the wrong person to the wrong screen, which is the whole reason
        // this probe exists in an adapter that refuses everything anyway.
        detail.Should().Contain("API key");
        detail.Should().Contain("per connection");
        detail.Should().NotContain(SabSpecification.MissingDocument);
        detail.Should().NotContain(SabSpecification.PlanReference);
    }

    private static ISecretsModule VaultHolding(SecretHint? hint)
    {
        var vault = Substitute.For<ISecretsModule>();

        vault.GetHintAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(hint));

        return vault;
    }
}
