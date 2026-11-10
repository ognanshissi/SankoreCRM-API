namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.CompanyInfo.QuoteIdentity;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Quote lines, lot 2 (2026-11-10): the institution's legal identity printed on quotes, the quote
/// settings, and the yearly numbering DEV-2026-000001.
/// </summary>
public sealed class QuoteIdentityTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public QuoteIdentityTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task SeedCompanyAsync()
    {
        await using var db = _factory.CreateContext();
        db.CompanyInfos.Add(CompanyInfo.Create(_tenant, "Lagune Négoce", "Négoce de matériaux"));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_legal_identity_is_saved_trimmed_and_read_by_the_quote_issuer()
    {
        await SeedCompanyAsync();
        await using (var db = _factory.CreateContext())
            (await new SetLegalIdentityHandler(db).Handle(new(
                " Lagune Négoce SA ", "SA", "Rue du Commerce\nBP 123 Abidjan 01", "+225 27 20 00 00 00",
                "devis@lagune.ci", "www.lagune.ci", "CI-ABJ-2019-B-12345", "1912345 A", "Réel normal",
                "Capital 10 000 000 FCFA"), CancellationToken.None)).IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var issuer = await new QuoteIssuerReader(read).GetAsync(_tenant, CancellationToken.None);
        issuer.DisplayName.Should().Be("Lagune Négoce SA");
        issuer.Rccm.Should().Be("CI-ABJ-2019-B-12345");
        issuer.TaxpayerNumber.Should().Be("1912345 A");
        issuer.HeadOfficeAddress.Should().Contain("BP 123");
        issuer.QuotePrefix.Should().Be("DEV");
        issuer.VatStandardRate.Should().Be(18m);
    }

    [Fact]
    public async Task A_blank_raison_sociale_prints_the_institution_name()
    {
        await SeedCompanyAsync();
        await using (var db = _factory.CreateContext())
            await new SetLegalIdentityHandler(db).Handle(new("  ", null, null, null, null, null, null, null, null, null), CancellationToken.None);

        await using var read = _factory.CreateContext();
        (await new QuoteIssuerReader(read).GetAsync(_tenant, CancellationToken.None)).DisplayName.Should().Be("Lagune Négoce");
    }

    [Fact]
    public async Task An_institution_never_saved_reads_the_defaults()
    {
        await using var db = _factory.CreateContext();
        var issuer = await new QuoteIssuerReader(db).GetAsync(_tenant, CancellationToken.None);

        issuer.QuotePrefix.Should().Be("DEV");
        issuer.QuoteValidityDays.Should().Be(30);
        issuer.VatReducedRate.Should().Be(9m);
    }

    [Fact]
    public async Task Numbers_follow_each_other_and_start_again_each_year()
    {
        await SeedCompanyAsync();
        await using var db = _factory.CreateContext();
        var issuer = new QuoteIssuerReader(db);

        (await issuer.NextNumberAsync(_tenant, new DateOnly(2026, 3, 1), CancellationToken.None)).Should().Be("DEV-2026-000001");
        (await issuer.NextNumberAsync(_tenant, new DateOnly(2026, 12, 31), CancellationToken.None)).Should().Be("DEV-2026-000002");
        (await issuer.NextNumberAsync(_tenant, new DateOnly(2027, 1, 1), CancellationToken.None)).Should().Be("DEV-2027-000001");
    }

    [Fact]
    public async Task The_prefix_is_the_institutions_and_the_preview_takes_nothing()
    {
        await SeedCompanyAsync();
        await using (var db = _factory.CreateContext())
            (await new SetQuoteSettingsHandler(db).Handle(new(" pro ", 15, "50 % à la commande"), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var settings = await new GetQuoteSettingsHandler(read).Handle(new(_tenant, _tenant, 2026), CancellationToken.None);
        settings.Should().Be(new QuoteSettingsDto("PRO", 15, "50 % à la commande", "PRO-2026-000001"));
        (await new QuoteIssuerReader(read).NextNumberAsync(_tenant, new DateOnly(2026, 5, 1), CancellationToken.None))
            .Should().Be("PRO-2026-000001");
    }

    [Theory]
    [InlineData("", 30, "QUOTE_PREFIX_INVALID")]
    [InlineData("DEV 1", 30, "QUOTE_PREFIX_INVALID")]
    [InlineData("-DEV", 30, "QUOTE_PREFIX_INVALID")]
    [InlineData("DEVISDEVISX", 30, "QUOTE_PREFIX_INVALID")]
    [InlineData("DEV", 0, "QUOTE_VALIDITY_INVALID")]
    [InlineData("DEV", 366, "QUOTE_VALIDITY_INVALID")]
    public void Quote_settings_are_checked(string prefix, int days, string error)
    {
        var info = CompanyInfo.Create(_tenant, "X", "Y");

        info.SetQuoteSettings(prefix, days, null).Error.Should().Be(error);
        info.QuotePrefix.Should().Be("DEV");
    }

    [Theory]
    [InlineData("www.lagune.ci", true)]
    [InlineData("https://lagune.ci/devis", true)]
    [InlineData("lagune", false)]
    [InlineData("ftp://lagune.ci", false)]
    [InlineData("www.lagune .ci", false)]
    public void A_website_is_a_web_address(string value, bool ok) =>
        SetLegalIdentityValidator.BeAWebAddress(value).Should().Be(ok);
}
