namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Segments;
using Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentDistribution;
using Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentRules;
using Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class SegmentRulesAndDistributionTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    // ── GET clients/segments/rules ───────────────────────────────────────────

    private static GetSegmentRulesHandler RulesHandler(ICustomerSettings settings)
        => new(settings, TestDoubles.CurrentUser(TenantId, UserId));

    [Fact]
    public async Task Lists_the_rules_by_ascending_priority()
    {
        var json = SegmentRuleEvaluation.Serialize(
        [
            new SegmentRuleDefinition("R-ALL", 30, "STANDARD"),
            new SegmentRuleDefinition("R-VIP", 10, "VIP", MinTenureDays: 1000),
        ]);

        var result = await RulesHandler(
                TestDoubles.Settings(TenantId, (CustomerSettingKeys.SegmentRulesJson, json)))
            .Handle(new GetSegmentRulesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsValid.Should().BeTrue();
        result.Value.Rules.Select(r => r.Code).Should().ContainInOrder("R-VIP", "R-ALL");
    }

    [Fact]
    public async Task Flags_a_rule_that_needs_outstanding_data_as_not_evaluable()
    {
        var json = SegmentRuleEvaluation.Serialize(
        [
            new SegmentRuleDefinition("R-WEALTH", 10, "WEALTH", RequiresOutstandingData: true),
            new SegmentRuleDefinition("R-ALL", 20, "STANDARD"),
        ]);

        var result = await RulesHandler(
                TestDoubles.Settings(TenantId, (CustomerSettingKeys.SegmentRulesJson, json)))
            .Handle(new GetSegmentRulesQuery(), CancellationToken.None);

        result.Value.EvaluableCount.Should().Be(1);
        result.Value.NotEvaluableCount.Should().Be(1);

        var parked = result.Value.Rules.Single(r => r.Code == "R-WEALTH");
        parked.IsEvaluable.Should().BeFalse();
        parked.NotEvaluableReason.Should().Be(SegmentRuleEvaluation.OutstandingDataUnavailableReason);

        result.Value.Rules.Single(r => r.Code == "R-ALL").IsEvaluable.Should().BeTrue();
    }

    [Fact]
    public async Task Reports_a_malformed_rule_set_as_invalid_rather_than_failing()
    {
        var result = await RulesHandler(
                TestDoubles.Settings(TenantId, (CustomerSettingKeys.SegmentRulesJson, "{ broken")))
            .Handle(new GetSegmentRulesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsValid.Should().BeFalse();
        result.Value.ParseError.Should().NotBeNullOrWhiteSpace();
        result.Value.Rules.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_the_factory_default_of_an_unconfigured_tenant_as_an_empty_rule_set()
    {
        var result = await RulesHandler(TestDoubles.Settings(TenantId))
            .Handle(new GetSegmentRulesQuery(), CancellationToken.None);

        result.Value.IsValid.Should().BeTrue();
        result.Value.Rules.Should().BeEmpty();
    }

    // ── PUT clients/segments/rules ───────────────────────────────────────────

    [Fact]
    public async Task Stores_the_rule_set_and_reports_how_many_rules_stay_inactive()
    {
        var settings = TestDoubles.Settings(TenantId);
        var handler = new UpdateSegmentRulesHandler(
            settings,
            TestDoubles.CurrentUser(TenantId, UserId),
            NullLogger<UpdateSegmentRulesHandler>.Instance);

        var result = await handler.Handle(
            new UpdateSegmentRulesCommand(
            [
                new SegmentRuleDefinition("R-ALL", 20, "STANDARD"),
                new SegmentRuleDefinition("R-WEALTH", 10, "WEALTH", RequiresOutstandingData: true),
            ]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RuleCount.Should().Be(2);
        result.Value.NotEvaluableCount.Should().Be(1);

        // Re-read through the same settings service: the value must round-trip.
        var stored = await settings.GetStringAsync(
            TenantId, CustomerSettingKeys.SegmentRulesJson, CancellationToken.None);

        SegmentRuleEvaluation.TryParse(stored, out var parsed, out _).Should().BeTrue();
        parsed.Select(r => r.Code).Should().ContainInOrder("R-WEALTH", "R-ALL");
    }

    [Fact]
    public async Task Accepts_an_empty_rule_set_which_disables_segmentation()
    {
        var handler = new UpdateSegmentRulesHandler(
            TestDoubles.Settings(TenantId),
            TestDoubles.CurrentUser(TenantId, UserId),
            NullLogger<UpdateSegmentRulesHandler>.Instance);

        var result = await handler.Handle(new UpdateSegmentRulesCommand([]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RuleCount.Should().Be(0);
    }

    // ── Validator ────────────────────────────────────────────────────────────

    private static readonly UpdateSegmentRulesValidator Validator = new();

    [Fact]
    public void Accepts_a_well_formed_rule_set()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [new SegmentRuleDefinition("R-VIP", 10, "VIP", MinTenureDays: 365, RequiredKycStatus: "Approved")]))
            .IsValid.Should().BeTrue();

    [Fact]
    public void Rejects_two_rules_sharing_a_code()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [
                new SegmentRuleDefinition("R-DUP", 10, "A"),
                new SegmentRuleDefinition("r-dup", 20, "B"),
            ]))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_an_empty_segment_code()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [new SegmentRuleDefinition("R-A", 10, "  ")]))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_an_unknown_kyc_status_so_a_typo_cannot_produce_a_rule_that_never_matches()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [new SegmentRuleDefinition("R-A", 10, "A", RequiredKycStatus: "Validated")]))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_an_unknown_risk_level()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [new SegmentRuleDefinition("R-A", 10, "A", RequiredRiskLevel: "Critical")]))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Rejects_an_inverted_tenure_window()
        => Validator.Validate(new UpdateSegmentRulesCommand(
            [new SegmentRuleDefinition("R-A", 10, "A", MinTenureDays: 400, MaxTenureDays: 100)]))
            .IsValid.Should().BeFalse();

    // ── GET clients/segments ─────────────────────────────────────────────────

    private GetSegmentDistributionHandler DistributionHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.CurrentUser(TenantId, UserId));

    private async Task SeedClientsAsync()
    {
        await using var seed = _factory.CreateContext();

        var premium = TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000001");
        premium.SetSegment("PREMIUM", DateTimeOffset.UtcNow);

        var standard1 = TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000002");
        standard1.SetSegment("STANDARD", DateTimeOffset.UtcNow);

        var standard2 = TimelineFixtures.Active(TenantId, OtherAgencyId, UserId, "AG000002-2026-000001");
        standard2.SetSegment("STANDARD", DateTimeOffset.UtcNow);

        // Never segmented, plus one archived (excluded) and one from another tenant (invisible).
        var unsegmented = TimelineFixtures.Active(TenantId, AgencyId, UserId, "AG000001-2026-000003");
        var archived = TimelineFixtures.Archived(TenantId, AgencyId, UserId, "AG000001-2026-000004");
        var foreign = TimelineFixtures.Active(OtherTenantId, AgencyId, UserId, "AG000001-2026-000005");
        foreign.SetSegment("PREMIUM", DateTimeOffset.UtcNow);

        seed.Clients.AddRange(premium, standard1, standard2, unsegmented, archived, foreign);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Counts_live_clients_per_segment_largest_first_with_the_unsegmented_bucket_last()
    {
        await SeedClientsAsync();

        var result = await DistributionHandler().Handle(
            new GetSegmentDistributionQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalClients.Should().Be(4, "archived, merged and other tenants are excluded");

        result.Value.Buckets.Select(b => b.SegmentCode).Should()
            .ContainInOrder("STANDARD", "PREMIUM", null);

        result.Value.Buckets.Single(b => b.SegmentCode == "STANDARD").ClientCount.Should().Be(2);
        result.Value.Buckets.Single(b => b.SegmentCode == "STANDARD").SharePercent.Should().Be(50m);
        result.Value.Buckets.Single(b => b.SegmentCode is null).ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Restricts_the_distribution_to_the_callers_agency_perimeter()
    {
        await SeedClientsAsync();

        var result = await DistributionHandler(AgencyId).Handle(
            new GetSegmentDistributionQuery(), CancellationToken.None);

        result.Value.TotalClients.Should().Be(3, "the client of the other agency is out of perimeter");
        result.Value.Buckets.Single(b => b.SegmentCode == "STANDARD").ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Returns_an_empty_distribution_rather_than_the_whole_tenant_for_a_user_who_sees_nothing()
    {
        await SeedClientsAsync();

        var result = await DistributionHandler(Guid.NewGuid()).Handle(
            new GetSegmentDistributionQuery(), CancellationToken.None);

        result.Value.TotalClients.Should().Be(0);
        result.Value.Buckets.Should().BeEmpty();
    }
}
