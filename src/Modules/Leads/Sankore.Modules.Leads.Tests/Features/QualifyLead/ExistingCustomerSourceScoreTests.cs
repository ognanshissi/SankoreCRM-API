namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// BNF-B-22: "le score F13.6 intègre la source « client existant »". A customer M01 already holds,
/// coming back for another product, is the strongest intent signal the source dimension carries.
/// </summary>
public sealed class ExistingCustomerSourceScoreTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public ExistingCustomerSourceScoreTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Capture(LeadSource source, Guid? customerId) => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: source,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        customerId: customerId,
        customerLinkIsAutomatic: customerId.HasValue);

    private async Task<int> SourceScoreAsync(Lead lead)
    {
        await using var db = _factory.CreateContext();
        var (_, factorsJson) = await new LeadScoreCalculator(db)
            .CalculateAsync(lead, CancellationToken.None);

        return JsonDocument.Parse(factorsJson).RootElement
            .GetProperty("sourceQuality").GetInt32();
    }

    [Fact]
    public async Task An_attached_file_import_scores_the_full_source_dimension()
    {
        // 5/20 on the source alone; 20/20 once it is recognised as an existing client's request.
        (await SourceScoreAsync(Capture(LeadSource.FileImport, Guid.NewGuid())))
            .Should().Be(20);
    }

    [Fact]
    public async Task An_unattached_file_import_still_scores_five()
    {
        // The measured ceiling of 45 for a file import, documented in
        // docs/plan-test-qualification-lead.md, describes an UNATTACHED lead and is unchanged.
        (await SourceScoreAsync(Capture(LeadSource.FileImport, customerId: null)))
            .Should().Be(5);
    }

    [Fact]
    public async Task A_walk_in_already_scored_the_maximum_and_is_unaffected()
    {
        // The attachment cannot raise the 100-point ceiling, because it is capped at the
        // dimension's existing maximum rather than added on top of it.
        (await SourceScoreAsync(Capture(LeadSource.Agency, customerId: null)))
            .Should().Be(20);
        (await SourceScoreAsync(Capture(LeadSource.Agency, Guid.NewGuid())))
            .Should().Be(20);
    }

    [Fact]
    public async Task The_source_dimension_never_exceeds_twenty()
    {
        foreach (var source in Enum.GetValues<LeadSource>())
        {
            (await SourceScoreAsync(Capture(source, Guid.NewGuid())))
                .Should().BeLessThanOrEqualTo(20, $"source {source} must stay inside its dimension");
        }
    }

    [Fact]
    public async Task An_attached_file_import_now_clears_the_historical_threshold()
    {
        // The point of the change: a known client asking for another product should be routed to
        // an agent without first waiting for activity history to accumulate.
        var lead = Capture(LeadSource.FileImport, Guid.NewGuid());

        await using var db = _factory.CreateContext();
        var (total, _) = await new LeadScoreCalculator(db).CalculateAsync(lead, CancellationToken.None);

        total.Should().BeGreaterThan(
            await UnattachedTotalAsync(),
            "the existing-customer signal is what lifts it");
    }

    private async Task<int> UnattachedTotalAsync()
    {
        await using var db = _factory.CreateContext();
        var (total, _) = await new LeadScoreCalculator(db)
            .CalculateAsync(Capture(LeadSource.FileImport, customerId: null), CancellationToken.None);
        return total;
    }
}
