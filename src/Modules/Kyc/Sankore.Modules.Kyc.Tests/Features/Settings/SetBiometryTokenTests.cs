namespace Sankore.Modules.Kyc.Tests.Features.Settings;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Features.Settings.SetBiometryToken;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The writer <c>BiometrySecrets</c> always assumed and which did not exist: nothing called
/// <c>ISecretsModule.SetAsync</c> for the biometry key, so <c>HttpBiometryClient</c> read a slot
/// nobody could fill and every verification answered BIOMETRY_NOT_CONFIGURED.
///
/// The property these tests exist for is not "the value is stored" — it is WHERE it is stored and,
/// above all, where it is NOT. A credential that reaches the audit table is worse than one that is
/// missing, because the audit table is the one built to be read back later.
/// </summary>
public sealed class SetBiometryTokenTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    private (SetBiometryTokenHandler Handler, ISecretsModule Secrets) Build()
    {
        var secrets = Substitute.For<ISecretsModule>();
        var handler = new SetBiometryTokenHandler(
            secrets,
            new FixedTenantContext(_tenantId),
            NullLogger<SetBiometryTokenHandler>.Instance);

        return (handler, secrets);
    }

    [Fact]
    public async Task Stores_the_token_under_this_tenants_vault_key()
    {
        var (handler, secrets) = Build();

        var result = await handler.Handle(
            new SetBiometryTokenCommand("biometry-dev-abc123"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await secrets.Received(1).SetAsync(
            Arg.Is<SecretKey>(k =>
                k.TenantId == _tenantId
                && k.Scope == BiometrySecrets.Scope
                && k.EntityId == Guid.Empty
                && k.Name == BiometrySecrets.TokenName),
            "biometry-dev-abc123",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Trims_the_token()
    {
        // A token pasted from a terminal or an email carries a trailing newline, and it would go
        // into the Authorization header as-is — a 401 whose cause is invisible on both sides.
        var (handler, secrets) = Build();

        await handler.Handle(
            new SetBiometryTokenCommand("  biometry-dev-abc123\n"), CancellationToken.None);

        await secrets.Received(1).SetAsync(
            Arg.Any<SecretKey>(), "biometry-dev-abc123", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_token_is_redacted_in_the_audit_payload()
    {
        // The command is an ICommand, so AuditBehavior serializes it into audit.entries through
        // SanitizedJsonSerializer. [property: SensitiveData] is what keeps the credential out of
        // that row; this test fails the moment someone removes the attribute.
        var json = SanitizedJsonSerializer.Serialize(
            new SetBiometryTokenCommand("biometry-dev-SUPER-SECRET"));

        json.Should().NotContain(
            "SUPER-SECRET", "the audit trail must never carry the credential itself");
        json.Should().Contain(
            SanitizedJsonSerializer.RedactedValue,
            "the property is kept, with its value replaced — so the audit still says a token was set");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_is_refused_rather_than_treated_as_a_clear(string token)
    {
        // Wiping a credential with an empty string is how a screen destroys one by accident.
        var result = new SetBiometryTokenValidator().Validate(new SetBiometryTokenCommand(token));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == nameof(SetBiometryTokenCommand.Token));
    }

    [Fact]
    public void A_token_longer_than_the_column_allows_is_refused()
    {
        var result = new SetBiometryTokenValidator()
            .Validate(new SetBiometryTokenCommand(new string('x', 513)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_plausible_token_passes_validation()
    {
        var result = new SetBiometryTokenValidator()
            .Validate(new SetBiometryTokenCommand(
                "biometry-dev-sB8myao71TAIoPvFMgebcfDQ3VG+eH6stv6KYlN0AqM="));

        result.IsValid.Should().BeTrue(
            "the shape of a token is the service's business, not ours — only emptiness is ours");
    }
}
