namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.Settings.KeyAccounts;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// A company is scored as a company (key accounts lot 2, 2026-11-09, product owner): its profile
/// counts its name, sector, headcount and contact rather than a date of birth and an identity
/// document, and the « Potentiel » dimension — 0 until the institution sets what a big prospect is —
/// tells a wholesaler from a corner shop.
/// </summary>
public sealed class CompanyScoreTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public CompanyScoreTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private Lead Company(decimal? purchases, string? sector = "WholesaleTrade", string? size = "Large")
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.ChangeProspectType(LeadType.Corporate);
        lead.UpdateCompany("Distribution Plateau", "achats@dplateau.ci", "+2252720300400", null);
        lead.DescribeCompany(sector, size, "Responsable des achats");
        if (purchases is { } p) lead.SetEstimatedAnnualPurchases(new Money(p, "XOF"));
        return lead;
    }

    private async Task SetAsync(LeadsDbContext db, decimal good, decimal major, params string[] sectors)
    {
        var result = await new KeyAccountSettingsHandler(new LeadSettingsService(db))
            .Handle(new SetKeyAccountSettingsCommand(_tenantId, good, major, sectors, Guid.NewGuid()), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        await db.SaveChangesAsync();
    }

    private static int Potential(string factorsJson)
        => JsonDocument.Parse(factorsJson).RootElement.GetProperty("potential").GetInt32();

    [Fact]
    public void A_company_earns_its_profile_from_what_a_company_has()
    {
        var company = Company(null);

        // Company 4, sector 3, size 2, company e-mail 2, position 2 — plus the contact's name 4 and
        // e-mail 3 when the builder set them: no date of birth or identity document is needed.
        var expected = 13
            + (!string.IsNullOrWhiteSpace(company.FirstName) || !string.IsNullOrWhiteSpace(company.LastName) ? 4 : 0)
            + (!string.IsNullOrWhiteSpace(company.Email) ? 3 : 0);
        LeadScoreCalculator.ProfileCompletenessScore(company).Should().Be(expected);

        var person = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        person.UpdateCompany("Distribution Plateau", null, null, null);
        LeadScoreCalculator.ProfileCompletenessScore(company)
            .Should().BeGreaterThan(LeadScoreCalculator.ProfileCompletenessScore(person),
                "the same company details are worth more on a company prospect than on a person");
    }

    [Fact]
    public void A_company_is_coherent_through_its_own_number()
    {
        var company = Company(null);
        var withPhone = LeadScoreCalculator.DeclarativeCoherenceScore(company);
        company.UpdateCompany(null, null, "", null);
        LeadScoreCalculator.DeclarativeCoherenceScore(company).Should().Be(withPhone - 3);
    }

    [Fact]
    public async Task The_potential_is_worth_nothing_until_the_institution_sets_it()
    {
        await using var db = _factory.CreateContext();
        var (_, factors) = await new LeadScoreCalculator(db).CalculateAsync(Company(90_000_000m), default);

        Potential(factors).Should().Be(0);
    }

    [Theory]
    [InlineData(90_000_000, "WholesaleTrade", "Large", 20)] // key account 12 + size 4 + targeted sector 4
    [InlineData(20_000_000, "WholesaleTrade", "Medium", 13)] // good customer 6 + size 3 + sector 4
    [InlineData(1_000_000, "Health", "Small", 1)]           // below both thresholds, sector not targeted
    [InlineData(null, null, null, 0)]
    public async Task The_potential_reads_purchases_size_and_sector(int? purchases, string? sector, string? size, int expected)
    {
        await using var db = _factory.CreateContext();
        await SetAsync(db, 10_000_000m, 50_000_000m, "WholesaleTrade", "Construction");

        var lead = Company(purchases, sector, size);
        var (_, factors) = await new LeadScoreCalculator(db).CalculateAsync(lead, default);

        Potential(factors).Should().Be(expected);
    }

    [Fact]
    public async Task A_person_never_has_a_potential()
    {
        await using var db = _factory.CreateContext();
        await SetAsync(db, 10_000_000m, 50_000_000m, "WholesaleTrade");

        var person = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        person.DescribeCompany("WholesaleTrade", "Large", null);
        person.SetEstimatedAnnualPurchases(new Money(90_000_000m, "XOF"));
        var (_, factors) = await new LeadScoreCalculator(db).CalculateAsync(person, default);

        Potential(factors).Should().Be(0);
    }

    [Theory]
    [InlineData(50_000_000, 10_000_000)] // good above major
    [InlineData(10_000_000, 10_000_000)] // equal
    [InlineData(-1, 0)]
    public async Task Thresholds_out_of_order_are_refused(int good, int major)
    {
        await using var db = _factory.CreateContext();
        var result = await new KeyAccountSettingsHandler(new LeadSettingsService(db))
            .Handle(new SetKeyAccountSettingsCommand(_tenantId, good, major, [], Guid.NewGuid()), default);

        result.Error.Should().Be(KeyAccountSettingsHandler.Invalid);
    }

    [Fact]
    public async Task An_unknown_sector_is_refused_and_a_label_is_stored_as_its_code()
    {
        await using var db = _factory.CreateContext();
        var handler = new KeyAccountSettingsHandler(new LeadSettingsService(db));

        (await handler.Handle(new SetKeyAccountSettingsCommand(_tenantId, 0, 0, ["Spatial"], Guid.NewGuid()), default))
            .Error.Should().Be("LEAD_ACTIVITY_SECTOR_INVALID");

        var ok = await handler.Handle(new SetKeyAccountSettingsCommand(_tenantId, 0, 0, ["BTP et construction"], Guid.NewGuid()), default);
        ok.Value.TargetSectors.Should().Equal("Construction");
        ok.Value.IsConfigured.Should().BeTrue("a targeted sector alone switches the dimension on");
    }

    [Fact]
    public async Task Saving_the_settings_rescores_the_open_company_prospects()
    {
        var big = Company(90_000_000m);
        var person = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        await using (var db = _factory.CreateContext())
        {
            foreach (var lead in new[] { big, person }) lead.RecordScoreUpdate(30);
            db.Leads.AddRange(big, person);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            await SetAsync(db, 10_000_000m, 50_000_000m, "WholesaleTrade");
            var rescored = await RescoreCompanyLeadsJob.RescoreAsync(db, _tenantId, default);
            rescored.Should().Be(1, "only the open company prospect");
        }

        await using var verify = _factory.CreateContext();
        var stored = await verify.Leads.SingleAsync(l => l.Id == big.Id);
        stored.Score.Should().NotBe(30);
        (await verify.ScoreHistories.CountAsync(h => h.LeadId == big.Id && h.TriggerEvent == RescoreCompanyLeadsJob.TriggerEvent))
            .Should().Be(1);
        (await verify.Leads.SingleAsync(l => l.Id == person.Id)).Score.Should().Be(30, "a person is not rescored");
    }
}
