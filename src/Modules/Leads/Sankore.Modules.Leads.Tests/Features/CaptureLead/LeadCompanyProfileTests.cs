namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Features.MergeLeads;
using Sankore.Modules.Leads.Features.UpdateLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// What a company prospect is — sector, headcount, yearly purchases, the contact's position (key
/// accounts lot 1, 2026-11-09, product owner) — reaches the lead from the creation form, an import, a
/// web form, an update and a merge, and filters the list.
/// </summary>
public sealed class LeadCompanyProfileTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public LeadCompanyProfileTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private CaptureLeadHandler CaptureHandler(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(c => $"idx_{c.Arg<string>()}");
        return new CaptureLeadHandler(
            db, NullLogger<CaptureLeadHandler>.Instance, TimeProvider.System, Substitute.For<IBus>(), indexer,
            TestLeadEmailIndexer.Instance,
            Substitute.For<Sankore.Modules.Customers.PublicApi.ICustomersModule>(),
            Substitute.For<IAdministrationModule>(),
            Substitute.For<IEventPublisher>());
    }

    private CaptureLeadCommand Corporate(string? sector = "Commerce de gros et distribution", string? size = "120") => new(
        TenantId: _tenantId,
        FullName: "Issa Traoré",
        PhoneNumber: "+2250709081199",
        Source: LeadSource.Agency,
        InterestedProduct: "Fournitures de bureau",
        PreferredLanguage: "fr",
        Latitude: 0,
        Longitude: 0,
        PreferredAgencyId: null,
        Force: true,
        ProspectType: LeadType.Corporate,
        CompanyName: " Distribution Plateau SARL ",
        CompanyEmail: "achats@dplateau.ci",
        CompanyPhone: "+2252720300400",
        Website: "https://dplateau.ci",
        ActivitySector: sector,
        CompanySize: size,
        EstimatedAnnualPurchases: 45_000_000m,
        ContactJobTitle: "Responsable des achats");

    [Fact]
    public async Task The_company_typed_at_capture_is_stored_on_the_lead()
    {
        // Lead.Capture wrote these columns empty and nothing wrote them afterwards: the company
        // typed on the creation form or read from an import was lost.
        await using var db = _factory.CreateContext();
        var result = await CaptureHandler(db).Handle(Corporate(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);

        await using var verify = _factory.CreateContext();
        var lead = await verify.Leads.SingleAsync(l => l.Id == result.Value.LeadId);

        lead.ProspectType.Should().Be(LeadType.Corporate);
        lead.CompanyName.Should().Be("Distribution Plateau SARL");
        lead.CompanyEmail.Should().Be("achats@dplateau.ci");
        lead.CompanyPhone.Should().Be("+2252720300400");
        lead.Website.Should().Be("https://dplateau.ci");
        lead.ActivitySectorCode.Should().Be("WholesaleTrade", "a label is stored as its code");
        lead.CompanySize.Should().Be(LeadCompanySize.Medium, "a headcount of 120 is the 50-249 bracket");
        lead.EstimatedAnnualPurchases.Should().Be(new Money(45_000_000m, Sankore.Shared.Kernel.InstitutionCurrencies.Default));
        lead.ContactJobTitle.Should().Be("Responsable des achats");
    }

    [Fact]
    public async Task An_unknown_sector_refuses_the_capture()
    {
        await using var db = _factory.CreateContext();
        var result = await CaptureHandler(db).Handle(Corporate(sector: "Spatial"), CancellationToken.None);

        result.Error.Should().Be("LEAD_ACTIVITY_SECTOR_INVALID");
        (await db.Leads.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Micro", LeadCompanySize.Micro)]
    [InlineData("large", LeadCompanySize.Large)]
    [InlineData("10 à 49 personnes", LeadCompanySize.Small)]
    [InlineData("9", LeadCompanySize.Micro)]
    [InlineData("49", LeadCompanySize.Small)]
    [InlineData("250", LeadCompanySize.Large)]
    [InlineData("1 200", LeadCompanySize.Large)]
    public void A_size_reads_a_bracket_its_label_or_a_headcount(string value, LeadCompanySize expected)
        => LeadCompanySizes.Parse(value).Should().Be(expected);

    [Theory]
    [InlineData("0")]
    [InlineData("beaucoup")]
    [InlineData("2")] // an enum's number is not a bracket: « 2 » people is Micro, never Medium
    public void A_size_that_is_not_a_bracket_is_a_headcount_or_nothing(string value)
    {
        var parsed = LeadCompanySizes.Parse(value);
        if (value == "2") parsed.Should().Be(LeadCompanySize.Micro);
        else parsed.Should().BeNull();
    }

    [Fact]
    public void The_form_offers_exactly_the_domain_sectors_and_sizes()
    {
        var form = NativeFormCatalog.For(FormEntity.Lead);

        form.Find("activitySector")!.Options!.Select(o => (o.Value, o.Label))
            .Should().Equal(LeadActivitySectors.All);
        form.Find("companySize")!.Options!.Select(o => o.Value)
            .Should().Equal(Enum.GetNames<LeadCompanySize>());
        form.Find("companySize")!.Options!.Select(o => o.Label)
            .Should().Equal(Enum.GetValues<LeadCompanySize>().Select(LeadCompanySizes.Label));
    }

    [Fact]
    public void The_company_fields_are_shown_for_a_company_only()
    {
        var form = NativeFormCatalog.For(FormEntity.Lead);
        var section = form.Sections.Single(s => s.Key == "company");

        section.VisibleWhen!.FieldKey.Should().Be("prospectType");
        foreach (var key in new[] { "activitySector", "companySize", "estimatedAnnualPurchases", "contactJobTitle" })
            form.Find(key)!.SectionKey.Should().Be("company");
    }

    // ── Import ───────────────────────────────────────────────────────────────

    private static ImportLeadRow Row() => new()
    {
        FullName          = "Issa Traoré",
        PhoneNumber       = "+2250709081199",
        InterestedProduct = "Fournitures de bureau",
    };

    [Fact]
    public void An_import_row_naming_a_company_is_a_company_prospect()
    {
        var result = LeadRowParser.Parse(Row() with
        {
            CompanyName = "Distribution Plateau SARL",
            ActivitySector = "BTP et construction",
            CompanySize = "Small",
            EstimatedAnnualPurchases = "12000000",
            ContactJobTitle = "Gérant",
        }, new ImportDefaults(), LeadSource.FileImport);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Row!.ProspectType.Should().Be(LeadType.Corporate);
        result.Row.ActivitySector.Should().Be("Construction");
        result.Row.CompanySize.Should().Be("Small");
        result.Row.EstimatedAnnualPurchases.Should().Be(12_000_000m);
        result.Row.ContactJobTitle.Should().Be("Gérant");
    }

    [Fact]
    public void An_import_row_says_its_type_and_refuses_an_unknown_sector()
    {
        LeadRowParser.Parse(Row() with { CompanyName = "Employeur SA", ProspectType = "Particulier" },
                new ImportDefaults(), LeadSource.FileImport)
            .Row!.ProspectType.Should().Be(LeadType.Individual);

        var bad = LeadRowParser.Parse(Row() with { ActivitySector = "Spatial", CompanySize = "beaucoup" },
            new ImportDefaults(), LeadSource.FileImport);
        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().HaveCount(2);
    }

    // ── Web form, webhook ────────────────────────────────────────────────────

    [Fact]
    public async Task A_web_form_describes_the_company_and_drops_what_it_cannot_read()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        var mapped = new Dictionary<string, string?>
        {
            ["prospectType"]             = "Entreprise",
            ["companyName"]              = "Hôtel Lagune",
            ["website"]                  = "https://lagune.ci",
            ["activitySector"]           = "hotellerie", // unreadable: dropped, the prospect is kept
            ["companySize"]              = "35",
            ["estimatedAnnualPurchases"] = "8 500 000",
            ["contactJobTitle"]          = "Économe",
        };

        await InboundCompanyProfile.ApplyAsync(lead, mapped, null, null, CancellationToken.None);

        lead.ProspectType.Should().Be(LeadType.Corporate);
        lead.CompanyName.Should().Be("Hôtel Lagune");
        lead.Website.Should().Be("https://lagune.ci");
        lead.ActivitySectorCode.Should().BeNull();
        lead.CompanySize.Should().Be(LeadCompanySize.Small);
        lead.EstimatedAnnualPurchases!.Amount.Should().Be(8_500_000m);
        lead.ContactJobTitle.Should().Be("Économe");
    }

    [Fact]
    public async Task A_payload_naming_a_company_without_a_type_is_a_company()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        await InboundCompanyProfile.ApplyAsync(lead,
            new Dictionary<string, string?> { ["companyName"] = "Clinique Avenir" }, null, null, CancellationToken.None);

        lead.ProspectType.Should().Be(LeadType.Corporate);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_update_turns_a_person_into_a_company_and_clears_what_it_is_asked_to()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.DescribeCompany("Health", "Large", "Pharmacien");
        lead.SetEstimatedAnnualPurchases(new Money(1_000_000m, "XOF"));
        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new UpdateLeadHandler(db, TestLeadEmailIndexer.Instance,
                    Substitute.For<IAdministrationModule>(), NullLogger<UpdateLeadHandler>.Instance)
                .Handle(new UpdateLeadCommand(lead.Id, null, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    ProspectType: LeadType.Corporate, ActivitySector: "Education", CompanySize: "",
                    ClearEstimatedAnnualPurchases: true), default);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var verify = _factory.CreateContext();
        var stored = await verify.Leads.SingleAsync(l => l.Id == lead.Id);
        stored.ProspectType.Should().Be(LeadType.Corporate);
        stored.ActivitySectorCode.Should().Be("Education");
        stored.CompanySize.Should().BeNull("blank clears");
        stored.ContactJobTitle.Should().Be("Pharmacien", "a field not sent is left alone");
        stored.EstimatedAnnualPurchases.Should().BeNull();
    }

    // ── Merge ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_merge_takes_the_company_profile_as_a_block()
    {
        var source = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        source.DescribeCompany("Technology", null, null);
        var target = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        target.DescribeCompany("Health", "Large", "Pharmacien");

        target.ApplyMergeOverrides(companyProfileOf: source);

        target.ActivitySectorCode.Should().Be("Technology");
        target.CompanySize.Should().BeNull("the block moves whole, never field by field");
        target.ContactJobTitle.Should().BeNull();
        new MergeFieldPreferences(TakeCompanyProfile: true).OverriddenFields().Should().Contain("companyProfile");
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_type_sector_and_size()
    {
        var shop = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        shop.ChangeProspectType(LeadType.Corporate);
        shop.DescribeCompany("RetailTrade", "Micro", null);
        var wholesaler = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        wholesaler.ChangeProspectType(LeadType.Corporate);
        wholesaler.DescribeCompany("WholesaleTrade", "Large", null);
        var person = LeadTestBuilder.Create().WithTenant(_tenantId).Build();

        await using var db = _factory.CreateContext();
        db.Leads.AddRange(shop, wholesaler, person);
        await db.SaveChangesAsync();

        async Task<List<Guid>> Ids(LeadListFilter filter)
            => await (await filter.ApplyAsync(db.Leads, db, null, null, default)).Value.Select(l => l.Id).ToListAsync();

        (await Ids(new LeadListFilter(ProspectType: LeadType.Corporate))).Should().BeEquivalentTo([shop.Id, wholesaler.Id]);
        (await Ids(new LeadListFilter(ActivitySector: "Commerce de gros et distribution"))).Should().Equal(wholesaler.Id);
        (await Ids(new LeadListFilter(CompanySize: LeadCompanySize.Micro))).Should().Equal(shop.Id);

        (await new LeadListFilter(ActivitySector: "Spatial").ApplyAsync(db.Leads, db, null, null, default))
            .Error.Should().Be("LEAD_ACTIVITY_SECTOR_INVALID");
    }
}
