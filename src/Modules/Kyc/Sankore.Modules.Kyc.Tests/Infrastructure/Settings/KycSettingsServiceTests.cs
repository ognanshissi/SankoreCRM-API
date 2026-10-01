namespace Sankore.Modules.Kyc.Tests.Infrastructure.Settings;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The KYC parameters live in this module rather than in M12 — a deliberate decision, since M12
/// exposes no generic settings store and a compliance ceiling belongs to the module that enforces
/// it. These tests pin the two properties that make that safe: a missing row still answers with
/// the declared default, and an unknown or ill-typed key is refused instead of stored.
/// </summary>
public sealed class KycSettingsServiceTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public KycSettingsServiceTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private KycSettingsService Service() => new(_factory.CreateContext(), TimeProvider.System);

    [Fact]
    public async Task A_key_with_no_row_answers_with_its_compiled_in_default()
    {
        // A tenant created between two seeder runs must not read an empty ceiling — an empty
        // ceiling parses as zero, which would block every operation of a simplified customer.
        var value = await Service().GetDecimalAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxBalance, CancellationToken.None);

        value.Should().Be(250_000m);
    }

    [Fact]
    public async Task Every_declared_key_has_a_usable_default()
    {
        var all = await Service().GetAllAsync(_tenantId, CancellationToken.None);

        all.Should().HaveCount(KycSettingKeys.Defaults.Count);
        all.Values.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
    }

    [Fact]
    public async Task A_stored_value_wins_over_the_default()
    {
        await Service().SetAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxBalance, "400000",
            Guid.NewGuid(), CancellationToken.None);

        (await Service().GetDecimalAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxBalance, CancellationToken.None))
            .Should().Be(400_000m);
    }

    [Fact]
    public async Task Setting_the_same_key_twice_updates_rather_than_duplicates()
    {
        var actor = Guid.NewGuid();
        var service = Service();

        await service.SetAsync(_tenantId, KycSettingKeys.ReviewGraceDays, "15", actor, CancellationToken.None);
        await Service().SetAsync(_tenantId, KycSettingKeys.ReviewGraceDays, "45", actor, CancellationToken.None);

        (await Service().GetIntAsync(_tenantId, KycSettingKeys.ReviewGraceDays, CancellationToken.None))
            .Should().Be(45);

        await using var db = _factory.CreateContext();
        db.KycSettings.Count(s => s.Key == KycSettingKeys.ReviewGraceDays).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_key_is_refused_not_stored()
    {
        var result = await Service().SetAsync(
            _tenantId, "max-something-invented", "1", Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SettingUnknown);

        await using var db = _factory.CreateContext();
        db.KycSettings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(KycSettingKeys.ReviewGraceDays, "trente")]
    [InlineData(KycSettingKeys.SimplifiedMaxBalance, "beaucoup")]
    [InlineData(KycSettingKeys.FaceMatchMaxAttempts, "")]
    public async Task A_value_that_does_not_match_its_declared_type_is_refused(string key, string value)
    {
        var result = await Service().SetAsync(_tenantId, key, value, Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SettingInvalidValue);
    }

    [Fact]
    public async Task A_setting_of_another_tenant_is_never_read()
    {
        var other = Guid.NewGuid();
        await Service().SetAsync(
            other, KycSettingKeys.SimplifiedMaxBalance, "999999", Guid.NewGuid(), CancellationToken.None);

        (await Service().GetDecimalAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxBalance, CancellationToken.None))
            .Should().Be(250_000m, "the other tenant's ceiling must not leak");
    }

    [Fact]
    public async Task Numbers_are_parsed_invariantly()
    {
        // A fr-FR host reads "250000,50" and "250000.50" differently; the stored form is invariant.
        await Service().SetAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxMonthlyFlow, "750000.50",
            Guid.NewGuid(), CancellationToken.None);

        (await Service().GetDecimalAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxMonthlyFlow, CancellationToken.None))
            .Should().Be(750_000.50m);
    }
}
