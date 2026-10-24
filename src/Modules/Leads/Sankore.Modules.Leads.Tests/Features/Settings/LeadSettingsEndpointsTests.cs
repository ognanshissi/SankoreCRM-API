namespace Sankore.Modules.Leads.Tests.Features.Settings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Settings;
using Sankore.Modules.Leads.Features.Settings.GetLeadSetting;
using Sankore.Modules.Leads.Features.Settings.ListLeadSettings;
using Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.Features.Retention;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The M13 tenant parameters, which until now were reachable only with SQL — M01 and M02 both
/// exposed theirs. It mattered most for <c>lead-retention-months</c>, which governs an
/// irreversible nightly destruction: "check the retention period before the first run" had
/// nowhere to be carried out.
/// </summary>
public sealed class LeadSettingsEndpointsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public LeadSettingsEndpointsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenant);
        user.Id.Returns(_actor);
        return user;
    }

    private async Task SeedRowAsync(string key, string value)
    {
        var declared = LeadSettingKeys.Find(key)!;

        await using var db = _factory.CreateContext();
        db.LeadSettings.Add(LeadSetting.Create(
            _tenant, declared.Key, value, declared.ValueType, declared.Description));
        await db.SaveChangesAsync();
    }

    // ── list ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_is_driven_by_the_catalogue_not_by_the_table()
    {
        // A tenant whose seeder has not run still sees every declared key, with the value every
        // reader resolves to. Showing nothing would make the screen disagree with the module.
        await using var db = _factory.CreateContext();

        var result = await new ListLeadSettingsHandler(db)
            .Handle(new ListLeadSettingsQuery(), CancellationToken.None);

        result.Value!.Should().HaveCount(LeadSettingKeys.Defaults.Count);
        result.Value.Should().OnlyContain(s => s.IsDefault);
        result.Value.Select(s => s.Key).Should().BeEquivalentTo(
            LeadSettingKeys.Defaults.Select(d => d.Key),
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_customised_value_is_reported_as_not_default()
    {
        await SeedRowAsync(LeadSettingKeys.LeadRetentionMonths, "36");

        await using var db = _factory.CreateContext();
        var result = await new ListLeadSettingsHandler(db)
            .Handle(new ListLeadSettingsQuery(), CancellationToken.None);

        var retention = result.Value!.Single(s => s.Key == LeadSettingKeys.LeadRetentionMonths);
        retention.Value.Should().Be("36");
        retention.DefaultValue.Should().Be("24");
        retention.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task A_stale_row_for_a_retired_key_is_not_exposed()
    {
        // Driven by the catalogue, so a screen cannot offer to edit something nothing reads.
        await using (var seed = _factory.CreateContext())
        {
            seed.LeadSettings.Add(LeadSetting.Create(
                _tenant, "retired-key", "whatever", LeadSettingKeys.TypeString, null));
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateContext();
        var result = await new ListLeadSettingsHandler(db)
            .Handle(new ListLeadSettingsQuery(), CancellationToken.None);

        result.Value!.Should().NotContain(s => s.Key == "retired-key");
    }

    // ── get ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_key_is_refused_rather_than_answered_with_an_empty_value()
    {
        await using var db = _factory.CreateContext();

        var result = await new GetLeadSettingHandler(db)
            .Handle(new GetLeadSettingQuery("no-such-setting"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadSettingErrors.Unknown);
    }

    [Fact]
    public async Task A_key_is_found_whatever_its_casing()
    {
        // The key arrives in a route segment, so the catalogue lookup is case-insensitive while
        // the column comparison stays exact — string equality in PostgreSQL is case-sensitive.
        await SeedRowAsync(LeadSettingKeys.LeadRetentionMonths, "36");

        await using var db = _factory.CreateContext();
        var result = await new GetLeadSettingHandler(db)
            .Handle(new GetLeadSettingQuery("Lead-Retention-Months"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Key.Should().Be(LeadSettingKeys.LeadRetentionMonths);
        result.Value.Value.Should().Be("36");
    }

    // ── update ──────────────────────────────────────────────────────────────

    private UpdateLeadSettingHandler Updater(LeadsDbContext db, ILeadSettings settings)
        => new(db, settings, User(), new FixedRetentionClock(Now));

    [Fact]
    public async Task An_update_goes_through_the_settings_service()
    {
        // Not straight to the table: the service owns the closed list, the type check, and the
        // eviction of its own process-local cache entry. Writing around it would leave readers
        // on the old value for up to a minute — a minute in which the previous retention period
        // still governs an irreversible job.
        var settings = Substitute.For<ILeadSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        await using var db = _factory.CreateContext();

        var result = await Updater(db, settings).Handle(
            new UpdateLeadSettingCommand(LeadSettingKeys.LeadRetentionMonths, " 36 "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await settings.Received(1).SetAsync(
            _tenant, LeadSettingKeys.LeadRetentionMonths, "36", _actor,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_key_is_refused_before_the_service_is_called()
    {
        var settings = Substitute.For<ILeadSettings>();

        await using var db = _factory.CreateContext();

        var result = await Updater(db, settings).Handle(
            new UpdateLeadSettingCommand("no-such-setting", "1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadSettingErrors.Unknown);
        await settings.DidNotReceive().SetAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_from_the_service_is_passed_through()
    {
        // A value that does not parse as the declared type is the service's refusal to make, and
        // the handler must not invent a success for it.
        var settings = Substitute.For<ILeadSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Fail("SETTING_INVALID_VALUE"));

        await using var db = _factory.CreateContext();

        var result = await Updater(db, settings).Handle(
            new UpdateLeadSettingCommand(LeadSettingKeys.LeadRetentionMonths, "trente-six"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SETTING_INVALID_VALUE");
    }

    [Fact]
    public async Task The_response_reflects_the_written_value_even_before_the_row_surfaces()
    {
        // The settings service may insert through its own DbContext. The value written is still
        // the authoritative one, so the response is built from it rather than answering
        // "unknown" for something just saved.
        var settings = Substitute.For<ILeadSettings>();
        settings.SetAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        await using var db = _factory.CreateContext();

        var result = await Updater(db, settings).Handle(
            new UpdateLeadSettingCommand(LeadSettingKeys.WhatsAppBotEnabled, "true"),
            CancellationToken.None);

        result.Value!.Value.Should().Be("true");
        result.Value.IsDefault.Should().BeFalse("the factory default is false");
        result.Value.UpdatedBy.Should().Be(_actor);
        result.Value.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public void Every_declared_key_is_reachable_through_Find()
    {
        // The catalogue and the lookup share one declaration, so a new setting cannot appear in
        // the list and be unreadable by key.
        foreach (var declared in LeadSettingKeys.Defaults)
            LeadSettingKeys.Find(declared.Key).Should().NotBeNull(declared.Key);

        LeadSettingKeys.Find(null).Should().BeNull();
        LeadSettingKeys.Find("  ").Should().BeNull();
    }
}
