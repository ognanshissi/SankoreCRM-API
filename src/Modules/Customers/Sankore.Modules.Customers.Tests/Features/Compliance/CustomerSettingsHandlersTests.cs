namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Settings.GetCustomerSetting;
using Sankore.Modules.Customers.Features.Compliance.Settings.ListCustomerSettings;
using Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class CustomerSettingsHandlersTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly ICurrentUser _currentUser;
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));

    public CustomerSettingsHandlersTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenantId);
        _currentUser = TestDoubles.CurrentUser(_tenantId, _userId, "Administrator");
    }

    public void Dispose() => _factory.Dispose();

    // ── Reads ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_returns_every_declared_key_even_before_the_tenant_has_been_seeded()
    {
        await using var db = _factory.CreateContext();
        var handler = new ListCustomerSettingsHandler(db);

        var result = await handler.Handle(new ListCustomerSettingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(s => s.Key)
            .Should().BeEquivalentTo(CustomerSettingKeys.Defaults.Select(d => d.Key));

        // Nothing stored yet, so every key is inherited from the factory defaults.
        result.Value.Should().OnlyContain(s => s.IsDefault);
    }

    [Fact]
    public async Task Listing_marks_a_customized_key_as_not_default_and_carries_its_declared_type()
    {
        await using var db = _factory.CreateContext();
        await StoreAsync(db, CustomerSettingKeys.MinimumAge, "21");

        var result = await new ListCustomerSettingsHandler(db)
            .Handle(new ListCustomerSettingsQuery(), CancellationToken.None);

        var minimumAge = result.Value.Single(s => s.Key == CustomerSettingKeys.MinimumAge);

        minimumAge.Value.Should().Be("21");
        minimumAge.DefaultValue.Should().Be("18");
        minimumAge.IsDefault.Should().BeFalse();
        minimumAge.ValueType.Should().Be(CustomerSettingKeys.TypeInt);
        minimumAge.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Listing_takes_the_value_type_from_the_catalogue_not_from_the_stored_row()
    {
        // A legacy row written with a wrong type must not make the admin screen render the wrong
        // widget: the catalogue is authoritative.
        await using var db = _factory.CreateContext();
        await StoreAsync(db, CustomerSettingKeys.MinimumAge, "21", valueType: "string");

        var result = await new ListCustomerSettingsHandler(db)
            .Handle(new ListCustomerSettingsQuery(), CancellationToken.None);

        result.Value.Single(s => s.Key == CustomerSettingKeys.MinimumAge)
            .ValueType.Should().Be(CustomerSettingKeys.TypeInt);
    }

    [Fact]
    public async Task Reading_an_unknown_key_answers_SETTING_UNKNOWN()
    {
        await using var db = _factory.CreateContext();

        var result = await new GetCustomerSettingHandler(db)
            .Handle(new GetCustomerSettingQuery("minimum-agee"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.SettingUnknown);
    }

    [Fact]
    public async Task Reading_a_known_key_is_case_insensitive_and_returns_the_canonical_key()
    {
        await using var db = _factory.CreateContext();

        var result = await new GetCustomerSettingHandler(db)
            .Handle(new GetCustomerSettingQuery("RETENTION-YEARS"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().Be(CustomerSettingKeys.RetentionYears);
        result.Value.Value.Should().Be("10");
    }

    // ── Writes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writing_an_unknown_key_answers_SETTING_UNKNOWN_and_touches_nothing()
    {
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand("not-a-setting", "1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.SettingUnknown);

        await settings.DidNotReceiveWithAnyArgs()
            .SetAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task Writing_a_valid_value_delegates_persistence_to_the_settings_service()
    {
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand(CustomerSettingKeys.MinimumAge, " 21 "),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("21");

        // Writing through the service (and not straight to the table) is what keeps its cache
        // from serving the previous value until the next restart.
        await settings.Received(1).SetAsync(
            _tenantId, CustomerSettingKeys.MinimumAge, "21", _userId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CustomerSettingKeys.MinimumAge, "eighteen")]
    [InlineData(CustomerSettingKeys.AdvisorFromConvertingAgent, "yes")]
    [InlineData(CustomerSettingKeys.BeneficialOwnerThreshold, "25,5")]
    [InlineData(CustomerSettingKeys.SegmentRulesJson, "{not json")]
    public async Task Writing_a_value_of_the_wrong_declared_type_is_refused(string key, string value)
    {
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand(key, value), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SETTING_VALUE_INVALID");

        await settings.DidNotReceiveWithAnyArgs()
            .SetAsync(default, default!, default!, default, default);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("9")]
    public async Task Retention_years_below_the_ten_year_floor_is_refused(string value)
    {
        // The floor is regulatory: a shorter value would let the anonymization endpoint erase a
        // client before the legal term.
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand(CustomerSettingKeys.RetentionYears, value),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SETTING_VALUE_OUT_OF_RANGE");
    }

    [Theory]
    [InlineData("10")]
    [InlineData("15")]
    public async Task Retention_years_at_or_above_the_floor_is_accepted(string value)
    {
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand(CustomerSettingKeys.RetentionYears, value),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_from_the_settings_service_is_surfaced_unchanged()
    {
        await using var db = _factory.CreateContext();
        var settings = Substitute.For<ICustomerSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(CustomerErrors.SettingUnknown)));

        var result = await NewUpdateHandler(db, settings)
            .Handle(new UpdateCustomerSettingCommand(CustomerSettingKeys.MinimumAge, "21"),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.SettingUnknown);
    }

    // ── Validator ───────────────────────────────────────────────────────────

    [Fact]
    public void The_validator_rejects_a_bad_typed_value_and_the_retention_floor()
    {
        var validator = new UpdateCustomerSettingValidator();

        validator.Validate(new UpdateCustomerSettingCommand(CustomerSettingKeys.MinimumAge, "abc"))
            .IsValid.Should().BeFalse();

        validator.Validate(new UpdateCustomerSettingCommand(CustomerSettingKeys.RetentionYears, "3"))
            .IsValid.Should().BeFalse();

        validator.Validate(new UpdateCustomerSettingCommand(CustomerSettingKeys.RetentionYears, "12"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_leaves_the_unknown_key_verdict_to_the_handler()
    {
        // An unknown key must surface as 404 SETTING_UNKNOWN, not as a generic 400 — so the
        // validator deliberately passes it through.
        new UpdateCustomerSettingValidator()
            .Validate(new UpdateCustomerSettingCommand("whatever-key", "whatever"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_accepts_every_factory_default_as_a_value_for_its_own_key()
    {
        // Sanity net over the whole catalogue: a default the validator would refuse means the
        // declared type and the declared value disagree.
        var validator = new UpdateCustomerSettingValidator();

        foreach (var declared in CustomerSettingKeys.Defaults)
        {
            validator.Validate(new UpdateCustomerSettingCommand(declared.Key, declared.Value))
                .IsValid.Should().BeTrue($"'{declared.Key}' default must satisfy its own type");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private UpdateCustomerSettingHandler NewUpdateHandler(
        CustomersDbContext db, ICustomerSettings settings) =>
        new(db, settings, _currentUser, _clock);

    private async Task StoreAsync(
        CustomersDbContext db, string key, string value, string? valueType = null)
    {
        var declared = CustomerSettingKeys.Defaults.Single(d => d.Key == key);

        db.CustomerSettings.Add(CustomerSetting.Create(
            _tenantId, key, value, valueType ?? declared.ValueType, declared.Description));

        await db.SaveChangesAsync();
    }
}
