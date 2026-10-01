namespace Sankore.Modules.Kyc.Tests.Features.Settings;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Settings.GetKycSetting;
using Sankore.Modules.Kyc.Features.Settings.ListKycSettings;
using Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

/// <summary>
/// The administration of the M02 parameters.
///
/// <para>
/// Three properties carry the weight here. The list is driven by the declared CATALOGUE, so a key
/// the seeder has not written yet still shows the value the module resolves to — a screen that
/// showed nothing would disagree with the enforcement. A well-typed but meaningless value is
/// refused, because <c>"int"</c> happily accepts a zero-day flow window and a zero face-match
/// attempt budget, neither of which fails where it was saved. And writing a ceiling invalidates the
/// per-customer limits cache of the whole tenant, so the new policy applies to the next operation
/// rather than to the one after the cache expires.
/// </para>
/// </summary>
public sealed class KycSettingsAdminTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly TimeProvider _clock = TimeProvider.System;

    public KycSettingsAdminTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private KycSettingsService Settings() => new(_factory.CreateContext(), _clock);

    private ICurrentUser CurrentUser()
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_actorId);
        user.TenantId.Returns(_tenantId);
        return user;
    }

    private UpdateKycSettingHandler UpdateHandler() => new(
        _factory.CreateContext(), Settings(), _cache, CurrentUser(), _clock);

    private KycModuleFacade Facade() => new(
        _factory.CreateContext(), Settings(), _cache, NullLogger<KycModuleFacade>.Instance);

    // ── reading ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_covers_every_declared_key_even_without_a_row()
    {
        // No seeder has run in this test: every key must still answer with the factory value, which
        // is the value KycSettingsService resolves to.
        var result = await new ListKycSettingsHandler(_factory.CreateContext())
            .Handle(new ListKycSettingsQuery(), default);

        var items = result.Value!;
        items.Should().HaveCount(KycSettingKeys.Defaults.Count);
        items.Should().OnlyContain(i => i.IsDefault && i.UpdatedAt == null);
        items.Single(i => i.Key == KycSettingKeys.SimplifiedMaxBalance).Value.Should().Be("250000");
    }

    [Fact]
    public async Task A_stored_value_is_listed_with_its_author_and_is_not_flagged_as_default()
    {
        await Settings().SetAsync(_tenantId, KycSettingKeys.SimplifiedMaxBalance, "90000", _actorId, default);

        var items = (await new ListKycSettingsHandler(_factory.CreateContext())
            .Handle(new ListKycSettingsQuery(), default)).Value!;

        var row = items.Single(i => i.Key == KycSettingKeys.SimplifiedMaxBalance);
        row.Value.Should().Be("90000");
        row.IsDefault.Should().BeFalse();
        row.UpdatedBy.Should().Be(_actorId);
        row.DefaultValue.Should().Be("250000", "a screen must be able to offer the factory value back");
    }

    [Fact]
    public async Task An_unknown_key_is_not_found_rather_than_an_empty_value()
    {
        var result = await new GetKycSettingHandler(_factory.CreateContext())
            .Handle(new GetKycSettingQuery("plafond-invente"), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SettingUnknown);
    }

    [Fact]
    public async Task A_key_is_found_whatever_its_case()
    {
        var result = await new GetKycSettingHandler(_factory.CreateContext())
            .Handle(new GetKycSettingQuery("SIMPLIFIED-ALERT-PCT"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Key.Should().Be(KycSettingKeys.SimplifiedAlertPct, "the stored key is canonical");
    }

    // ── writing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_ceiling_is_written_and_answered_back()
    {
        var result = await UpdateHandler().Handle(
            new UpdateKycSettingCommand(KycSettingKeys.SimplifiedMaxBalance, " 120000 "), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("120000", "the value is trimmed before it is stored");
        result.Value.IsDefault.Should().BeFalse();

        (await Settings().GetDecimalAsync(_tenantId, KycSettingKeys.SimplifiedMaxBalance, default))
            .Should().Be(120_000m);
    }

    [Fact]
    public async Task Writing_an_unknown_key_answers_the_contract_code()
    {
        var result = await UpdateHandler().Handle(
            new UpdateKycSettingCommand("plafond-invente", "1"), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SettingUnknown);
    }

    [Theory]
    // Each of these parses as its declared type and means something nobody wants.
    [InlineData(KycSettingKeys.SimplifiedMaxBalance, "0")]
    [InlineData(KycSettingKeys.SimplifiedMaxMonthlyFlow, "-5000")]
    [InlineData(KycSettingKeys.SimplifiedFlowWindowDays, "0")]
    [InlineData(KycSettingKeys.SimplifiedAlertPct, "0")]
    [InlineData(KycSettingKeys.SimplifiedAlertPct, "150")]
    [InlineData(KycSettingKeys.FaceMatchMaxAttempts, "0")]
    [InlineData(KycSettingKeys.ReviewYearsStandard, "0")]
    [InlineData(KycSettingKeys.VerificationRejectionFloor, "101")]
    [InlineData(KycSettingKeys.CapsCurrency, "franc CFA")]
    public async Task A_well_typed_but_meaningless_value_is_refused(string key, string value)
    {
        var result = await UpdateHandler().Handle(new UpdateKycSettingCommand(key, value), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SettingValueOutOfRange,
            "a type check alone would store it, and nothing would fail where it was saved");

        await using var db = _factory.CreateContext();
        db.KycSettings.Should().BeEmpty("a refused value must not leave a row behind");
    }

    [Fact]
    public async Task Zero_face_match_attempts_would_have_added_a_manager_to_every_low_risk_file()
    {
        // The one clause that lifts the low-risk ladder is reaching face-match-max-attempts. At zero
        // it is reached before the first capture, which quietly puts the branch manager on every
        // file — the kind of change that surfaces in an approval circuit, not at the save button.
        var result = await UpdateHandler().Handle(
            new UpdateKycSettingCommand(KycSettingKeys.FaceMatchMaxAttempts, "0"), default);

        result.Error.Should().Be(KycErrors.SettingValueOutOfRange);
    }

    // ── the cache ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lowering_a_ceiling_takes_effect_at_once_and_not_in_five_minutes()
    {
        await SeedSimplifiedFileAsync();

        // Warm the per-customer entry: this is what would otherwise keep serving 250 000.
        (await Facade().GetLimitsAsync(_tenantId, _customerId, default))!.MaxBalance
            .Should().Be(250_000m);

        await UpdateHandler().Handle(
            new UpdateKycSettingCommand(KycSettingKeys.SimplifiedMaxBalance, "90000"), default);

        (await Facade().GetLimitsAsync(_tenantId, _customerId, default))!.MaxBalance
            .Should().Be(90_000m,
                "every operation accepted against the revoked ceiling is one a regulator would ask about");
    }

    [Fact]
    public async Task Another_tenant_keeps_its_cached_ceilings()
    {
        // The generation is per tenant: one administrator's change must not cost every other tenant
        // its cache, and must certainly not expose them to each other's values.
        await SeedSimplifiedFileAsync();
        await Facade().GetLimitsAsync(_tenantId, _customerId, default);

        var otherTenant = Guid.NewGuid();
        var generationBefore = await KycModuleFacade.ReadLimitsGenerationAsync(_cache, otherTenant, default);

        await UpdateHandler().Handle(
            new UpdateKycSettingCommand(KycSettingKeys.SimplifiedMaxBalance, "90000"), default);

        (await KycModuleFacade.ReadLimitsGenerationAsync(_cache, otherTenant, default))
            .Should().Be(generationBefore);
    }

    private async Task SeedSimplifiedFileAsync()
    {
        var submitter = Guid.NewGuid();
        var file = KycFile.Open(_tenantId, _customerId, KycChannel.Agency, submitter, _clock);
        file.SubmitForVerification(submitter, _clock);
        file.RecordVerification(90, KycConfidenceLevel.High, _clock);
        file.Approve(KycTier.Simplified, Guid.NewGuid(), _clock);

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);
        await db.SaveChangesAsync();
    }
}
