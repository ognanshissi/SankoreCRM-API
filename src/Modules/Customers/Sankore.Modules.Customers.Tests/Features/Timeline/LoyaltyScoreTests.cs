namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Loyalty;
using Sankore.Modules.Customers.Features.Timeline.Loyalty.ComputeLoyaltyScores;
using Sankore.Modules.Customers.Features.Timeline.Loyalty.GetLoyaltyScore;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class LoyaltyScoreTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 3, 30, 0, TimeSpan.Zero);

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ComputeLoyaltyScoresHandler BuildHandler(string? weightsJson = null)
        => new(
            _factory.CreateContext(),
            weightsJson is null
                ? TestDoubles.Settings(TenantId)
                : TestDoubles.Settings(TenantId, (CustomerSettingKeys.LoyaltyWeightsJson, weightsJson)),
            new FixedTimeProvider(Now),
            NullLogger<ComputeLoyaltyScoresHandler>.Instance);

    private async Task<Client> SeedAsync(
        Guid tenantId, Guid agencyId, DateTimeOffset createdAt, int timelineFacts,
        string clientNumber = "AG000001-2026-000001")
    {
        var client = TimelineFixtures.Active(tenantId, agencyId, UserId, clientNumber).BackdatedTo(createdAt);

        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);

        for (var i = 0; i < timelineFacts; i++)
            seed.ClientTimelineEntries.Add(TimelineFixtures.Entry(
                tenantId, client.Id, Now.AddDays(-15 * (i + 1)), dedupKey: $"{client.Id:D}-fact-{i}"));

        await seed.SaveChangesAsync();
        return client;
    }

    // ── Weights & normalization ──────────────────────────────────────────────

    [Fact]
    public async Task Scores_a_long_standing_and_regular_client_at_the_top_of_the_scale()
    {
        // 6 years of tenure (saturates at 5) and 12 facts in the trailing year (saturates at 12).
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-6), timelineFacts: 12);

        var result = await BuildHandler().Handle(
            new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ClientsScored.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).LoyaltyScore.Should().Be(100);
    }

    [Fact]
    public async Task Scores_a_brand_new_inactive_client_at_zero()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now, timelineFacts: 0);

        await BuildHandler().Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).LoyaltyScore.Should().Be(0);
    }

    [Fact]
    public async Task Normalizes_over_the_available_weights_so_a_perfect_client_is_not_capped_at_sixty()
    {
        // Factory weights are tenure 30 + regularity 30 + volume 20 + products 20. If the two
        // missing components stayed in the denominator, the best possible score would be 60.
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-6), timelineFacts: 12);

        await BuildHandler("""{"tenure":30,"regularity":30,"volume":20,"products":20}""")
            .Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).LoyaltyScore.Should().Be(100);
    }

    [Fact]
    public async Task Respects_the_relative_weight_of_tenure_against_regularity()
    {
        // Full tenure, zero regularity, and tenure weighs three times regularity → 75.
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-6), timelineFacts: 0);

        await BuildHandler("""{"tenure":75,"regularity":25,"volume":0,"products":0}""")
            .Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).LoyaltyScore.Should().Be(75);
    }

    [Fact]
    public async Task Falls_back_to_the_factory_weights_when_the_setting_is_malformed()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-6), timelineFacts: 12);

        await BuildHandler("{ not json")
            .Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == client.Id)).LoyaltyScore.Should().Be(100);
    }

    // ── Provisional flag ─────────────────────────────────────────────────────

    [Fact]
    public async Task Marks_a_client_younger_than_ninety_days_as_provisional()
    {
        var young = await SeedAsync(TenantId, AgencyId, Now.AddDays(-30), 0, "AG000001-2026-000001");
        var settled = await SeedAsync(TenantId, AgencyId, Now.AddDays(-200), 0, "AG000001-2026-000002");

        var result = await BuildHandler().Handle(
            new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        result.Value.ProvisionalCount.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        (await assertions.Clients.SingleAsync(c => c.Id == young.Id))
            .LoyaltyScoreProvisional.Should().BeTrue();
        (await assertions.Clients.SingleAsync(c => c.Id == settled.Id))
            .LoyaltyScoreProvisional.Should().BeFalse();
    }

    // ── Breakdown & history ──────────────────────────────────────────────────

    [Fact]
    public async Task Records_volume_and_products_as_unavailable_in_the_breakdown()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-2), timelineFacts: 6);

        var result = await BuildHandler().Handle(
            new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        result.Value.UnavailableComponents.Should().BeEquivalentTo(new[] { "volume", "products" });

        await using var assertions = _factory.CreateContext();
        var snapshot = await assertions.ClientLoyaltyScores.SingleAsync(s => s.ClientId == client.Id);

        using var document = JsonDocument.Parse(snapshot.BreakdownJson);
        var root = document.RootElement;

        var components = root.GetProperty("components").EnumerateArray().ToList();

        // The four keys are always there: an absent component would be indistinguishable
        // from one that legitimately scored zero.
        components.Select(c => c.GetProperty("component").GetString()).Should()
            .ContainInOrder("tenure", "regularity", "volume", "products");

        foreach (var name in new[] { "tenure", "regularity" })
        {
            var component = components.Single(c => c.GetProperty("component").GetString() == name);
            component.GetProperty("isAvailable").GetBoolean().Should().BeTrue();
            component.GetProperty("rawValue").ValueKind.Should().Be(JsonValueKind.Number);
        }

        foreach (var name in new[] { "volume", "products" })
        {
            var component = components.Single(c => c.GetProperty("component").GetString() == name);
            component.GetProperty("isAvailable").GetBoolean().Should().BeFalse();
            component.GetProperty("points").GetDouble().Should().Be(0);
            component.GetProperty("rawValue").ValueKind.Should().Be(JsonValueKind.Null);
            component.GetProperty("unavailableReason").GetString().Should().Be("MODULE_NOT_AVAILABLE");
        }

        // …and repeated at the top level, so "what was missing" is readable without
        // walking the array.
        root.GetProperty("unavailableComponents").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "volume", "products" });
    }

    [Fact]
    public async Task Historizes_every_computation_instead_of_overwriting_the_previous_one()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-2), timelineFacts: 3);

        await BuildHandler().Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);
        await BuildHandler().Handle(new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.ClientLoyaltyScores.CountAsync(s => s.ClientId == client.Id)).Should().Be(2);
    }

    // ── Scope ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skips_archived_clients_and_clients_of_another_tenant()
    {
        var mine = await SeedAsync(TenantId, AgencyId, Now.AddYears(-2), 2, "AG000001-2026-000001");
        var foreign = await SeedAsync(OtherTenantId, AgencyId, Now.AddYears(-2), 2, "AG000001-2026-000002");

        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(TimelineFixtures.Archived(TenantId, AgencyId, UserId, "AG000001-2026-000003"));
            await seed.SaveChangesAsync();
        }

        var result = await BuildHandler().Handle(
            new ComputeLoyaltyScoresCommand(TenantId), CancellationToken.None);

        result.Value.ClientsScored.Should().Be(1);

        await using var assertions = _factory.CreateContext();
        var all = await assertions.Clients.IgnoreQueryFilters().ToListAsync();

        all.Single(c => c.Id == mine.Id).LoyaltyScore.Should().NotBeNull();
        all.Single(c => c.Id == foreign.Id).LoyaltyScore.Should().BeNull();
    }

    // ── GET clients/{id}/loyalty-score ───────────────────────────────────────

    private GetLoyaltyScoreHandler ReadHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.CurrentUser(TenantId, UserId));

    [Fact]
    public async Task Returns_the_latest_score_its_breakdown_and_the_previous_computations()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-2), timelineFacts: 4);

        await using (var seed = _factory.CreateContext())
        {
            seed.ClientLoyaltyScores.AddRange(
                ClientLoyaltyScore.Record(TenantId, client.Id, 40, false, """{"v":1}""", Now.AddDays(-60)),
                ClientLoyaltyScore.Record(TenantId, client.Id, 55, false, """{"v":2}""", Now.AddDays(-30)),
                ClientLoyaltyScore.Record(TenantId, client.Id, 61, false, """{"v":3}""", Now));
            await seed.SaveChangesAsync();
        }

        var result = await ReadHandler().Handle(
            new GetLoyaltyScoreQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Score.Should().Be(61);
        result.Value.ComputedAt.Should().Be(Now);
        result.Value.Breakdown.Should().Be("""{"v":3}""");
        result.Value.History.Select(h => h.Score).Should().ContainInOrder(61, 55, 40);
        result.Value.UnavailableComponents.Should().BeEquivalentTo(new[] { "volume", "products" });
    }

    [Fact]
    public async Task Returns_a_null_score_for_a_client_never_scored_yet()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-1), timelineFacts: 0);

        var result = await ReadHandler().Handle(
            new GetLoyaltyScoreQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Score.Should().BeNull();
        result.Value.IsProvisional.Should().BeFalse("unknown is not the same as provisional");
        result.Value.History.Should().BeEmpty();
    }

    [Fact]
    public async Task Caps_the_returned_history_length()
    {
        var client = await SeedAsync(TenantId, AgencyId, Now.AddYears(-2), timelineFacts: 0);

        await using (var seed = _factory.CreateContext())
        {
            for (var i = 0; i < 8; i++)
                seed.ClientLoyaltyScores.Add(ClientLoyaltyScore.Record(
                    TenantId, client.Id, i, false, "{}", Now.AddDays(-i)));
            await seed.SaveChangesAsync();
        }

        var result = await ReadHandler().Handle(
            new GetLoyaltyScoreQuery(client.Id, HistoryLimit: 3), CancellationToken.None);

        result.Value.History.Should().HaveCount(3);
    }

    [Fact]
    public async Task Answers_client_not_found_outside_the_agency_perimeter()
    {
        var client = await SeedAsync(TenantId, OtherAgencyId, Now.AddYears(-1), 0);

        var result = await ReadHandler(AgencyId).Handle(
            new GetLoyaltyScoreQuery(client.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Never_returns_the_score_of_a_client_of_another_tenant()
    {
        var foreign = await SeedAsync(OtherTenantId, AgencyId, Now.AddYears(-1), 0);

        var result = await ReadHandler().Handle(
            new GetLoyaltyScoreQuery(foreign.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ── Weights parsing ──────────────────────────────────────────────────────

    [Fact]
    public void Parses_the_tenant_weights()
    {
        var weights = LoyaltyWeights.Parse("""{"tenure":40,"regularity":20,"volume":25,"products":15}""", out var error);

        error.Should().BeNull();
        weights.Tenure.Should().Be(40);
        weights.Regularity.Should().Be(20);
        weights.AvailableWeightTotal.Should().Be(60);
    }

    [Fact]
    public void Treats_all_zero_available_weights_as_unset()
        => LoyaltyWeights.Parse("""{"tenure":0,"regularity":0,"volume":50,"products":50}""", out _)
            .Should().Be(LoyaltyWeights.Default);

    [Fact]
    public void Falls_back_to_the_defaults_on_malformed_weights()
    {
        var weights = LoyaltyWeights.Parse("{ nope", out var error);

        error.Should().NotBeNullOrWhiteSpace();
        weights.Should().Be(LoyaltyWeights.Default);
    }
}
