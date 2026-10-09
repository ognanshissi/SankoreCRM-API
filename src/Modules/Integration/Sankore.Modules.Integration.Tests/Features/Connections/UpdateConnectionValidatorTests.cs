namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using FluentValidation.Results;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The update validator shares every per-kind settings rule with the create one — they derive
/// from the same base, so the two cannot drift. What is tested here is what is ITS own: the
/// concurrency token, and the fact that the settings/kind agreement is deliberately NOT checked
/// at this level.
/// </summary>
public sealed class UpdateConnectionValidatorTests
{
    private readonly UpdateConnectionValidator _validator = new();

    private static UpdateConnectionCommand Command(
        ConnectionSettings? settings = null,
        uint? version = 1,
        DateTimeOffset? updatedAt = null,
        IntegrationMode mode = IntegrationMode.Api,
        string name = "Connexion")
        => new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            name,
            mode,
            // Complete, including the OAuth coordinates: OAuthClientCredentials is the default
            // auth mode, so a settings object without them is legitimately refused and every
            // test below would then be measuring that instead of what it claims to.
            settings ?? new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                TokenEndpoint = "https://cbs.example.ci/oauth/token",
                OAuthClientId = "sankore",
            },
            version,
            updatedAt);

    private ValidationResult Validate(UpdateConnectionCommand cmd) => _validator.Validate(cmd);

    private static IEnumerable<string> Keys(ValidationResult result)
        => result.Errors.Select(e => e.PropertyName);

    [Fact]
    public void An_update_carrying_a_row_version_is_valid()
        => Validate(Command()).IsValid.Should().BeTrue();

    [Fact]
    public void An_update_carrying_only_an_updated_at_is_valid_too()
        => Validate(Command(version: null, updatedAt: DateTimeOffset.UnixEpoch))
            .IsValid.Should().BeTrue();

    [Fact]
    public void An_update_with_no_concurrency_token_at_all_is_refused()
    {
        var result = Validate(Command(version: null, updatedAt: null));

        // Two administrators editing the same connection would otherwise each silently overwrite
        // the other.
        Keys(result).Should().Contain("expectedVersion");
    }

    [Fact]
    public void An_empty_connection_id_is_refused()
    {
        var result = _validator.Validate(
            new UpdateConnectionCommand(
                Guid.Empty, "X", IntegrationMode.Api,
                new TemenosSettings { BaseUrl = "https://cbs.example.ci/api/" },
                ExpectedVersion: 1));

        Keys(result).Should().Contain("connectionId");
    }

    [Fact]
    public void The_per_kind_rules_are_inherited_from_the_shared_base()
    {
        // Same failure key as the create validator produces: the rules live in one place.
        var result = Validate(Command(new SabSettings()));

        Keys(result).Should().Contain("settings.baseUrl");
        Keys(result).Should().Contain("settings.entity");
    }

    [Fact]
    public void Batch_rules_apply_to_an_update_as_well()
    {
        var result = Validate(Command(new PerfectVisionSettings(), mode: IntegrationMode.Batch));

        Keys(result).Should().Contain("settings.sftpHost");
    }

    [Fact]
    public void Settings_of_a_different_kind_pass_validation_and_are_refused_by_the_handler()
    {
        // The stored row's kind is in the database, which no validator can see. The handler
        // answers INTEGRATION_SETTINGS_INVALID — see UpdateConnectionHandlerTests.
        var result = Validate(Command(new AmplitudeSettings()));

        result.IsValid.Should().BeTrue(string.Join(", ", Keys(result)));
    }
}
