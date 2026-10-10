namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel.ValueObject;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers <c>IAdministrationModule.ResolveProductLineAsync</c> and
/// <c>GetBeneficiaryPolicyAsync</c> (BNF-B-06, BNF-B-01).
/// </summary>
public sealed class ProductLineResolutionTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ProductLineResolutionTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    /// <summary>A cache with no IDistributedCache: every read falls through to the query.</summary>
    private AdministrationModuleFacade Facade()
        => new(_factory.CreateContext(), new ProductCatalogCache());

    private async Task<ProductSpeciality> SeedAsync(
        string code,
        string name,
        ProductCategory category = ProductCategory.Insurance,
        bool active = true,
        BeneficiaryPolicy? policy = null,
        bool allowMultipleHoldings = true)
    {
        var product = ProductSpeciality.Create(_tenant, name, code, category);

        if (policy is not null)
            product.SetBeneficiaryPolicy(policy);
        product.SetAllowMultipleHoldings(allowMultipleHoldings);
        if (!active)
            product.Retire(DateOnly.FromDateTime(DateTime.UtcNow));

        await using var db = _factory.CreateContext();
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync(CancellationToken.None);
        return product;
    }

    // ── Label resolution ──────────────────────────────────────────────────

    [Fact]
    public async Task Resolves_an_exact_code()
    {
        var product = await SeedAsync("CRED-COM-01", "Crédit commerçant");

        (await Facade().ResolveProductLineAsync(_tenant, "CRED-COM-01", CancellationToken.None))
            .Should().Be(product.Id);
    }

    [Theory]
    [InlineData("Crédit commerçant")]
    [InlineData("CREDIT COMMERCANT")]   // accents stripped
    [InlineData("  crédit   commerçant  ")]  // padding and inner runs collapsed
    public async Task Resolves_a_name_whatever_its_casing_accents_or_spacing(string label)
    {
        // The two sides are written by different people: an agent types the label, an administrator
        // entered the catalogue. Comparing them raw resolves almost nothing.
        var product = await SeedAsync("CRED-COM-01", "Crédit commerçant");

        (await Facade().ResolveProductLineAsync(_tenant, label, CancellationToken.None))
            .Should().Be(product.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Épargne projet")]   // no such product
    public async Task Resolves_nothing_when_nothing_matches_exactly(string? label)
    {
        await SeedAsync("CRED-COM-01", "Crédit commerçant");

        (await Facade().ResolveProductLineAsync(_tenant, label, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Unknown_resolves_to_nothing_even_if_a_product_is_named_that()
    {
        // "Unknown" is the literal default the pull job and the system capture endpoint write when
        // a source names no product. Treating it as a label would funnel every unlabelled lead in
        // the deployment onto that product's policy.
        await SeedAsync("UNKNOWN", "Unknown");

        (await Facade().ResolveProductLineAsync(_tenant, "Unknown", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_retired_line_no_longer_resolves()
    {
        // Resolving a label onto a product that is no longer sold would apply its policy to a
        // brand-new request.
        await SeedAsync("CRED-COM-01", "Crédit commerçant", active: false);

        (await Facade().ResolveProductLineAsync(_tenant, "CRED-COM-01", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_ambiguous_name_resolves_to_nothing_rather_than_to_a_row_order()
    {
        // Two lines plausibly share a name. Resolving to whichever came back first would apply one
        // product's conversion gates to a request about the other.
        await SeedAsync("CRED-A", "Crédit commerçant");
        await SeedAsync("CRED-B", "Crédit commerçant");

        (await Facade().ResolveProductLineAsync(_tenant, "Crédit commerçant", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_code_match_wins_over_a_name_match()
    {
        // A code is an identifier an administrator chose; a name is a label.
        var byCode = await SeedAsync("EPARGNE", "Épargne libre");
        await SeedAsync("EPG-002", "Epargne");

        (await Facade().ResolveProductLineAsync(_tenant, "EPARGNE", CancellationToken.None))
            .Should().Be(byCode.Id);
    }

    [Fact]
    public async Task Another_tenants_product_never_resolves()
    {
        await SeedAsync("CRED-COM-01", "Crédit commerçant");

        (await Facade().ResolveProductLineAsync(Guid.NewGuid(), "CRED-COM-01", CancellationToken.None))
            .Should().BeNull();
    }

    // ── Policy read ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_a_configured_policy()
    {
        var policy = BeneficiaryPolicy.Create(
            householdEnabled: true,
            allowedRoles: [new DesignationRoleDefinition(
                "BENEFICIAIREDECES", "Bénéficiaire décès", 1, 4, SharesMustTotal100: true)],
            maxChildren: 10,
            childMaxAge: 21,
            requiredFieldsBeforeConversion: [PersonFieldCode.BirthDate],
            requiredKycTier: "Full",
            integrationSystem: "Orass").Value;

        var product = await SeedAsync(
            "SANTE-01", "Assurance santé famille",
            policy: policy, allowMultipleHoldings: false);

        var dto = await Facade().GetBeneficiaryPolicyAsync(
            _tenant, product.Id, CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.ProductCode.Should().Be("SANTE-01");
        dto.Category.Should().Be(nameof(ProductCategory.Insurance));
        dto.HouseholdEnabled.Should().BeTrue();
        dto.AllowMultipleHoldings.Should().BeFalse();
        dto.AllowedRoles.Should().ContainSingle();
        dto.AllowedRoles[0].SharesMustTotal100.Should().BeTrue();
        dto.MaxChildren.Should().Be(10);
        dto.ChildMaxAge.Should().Be(21);
        dto.RequiredFieldsBeforeConversion.Should().BeEquivalentTo([PersonFieldCode.BirthDate]);
        dto.RequiredKycTier.Should().Be("Full");
        dto.IntegrationSystem.Should().Be("Orass");
    }

    [Fact]
    public async Task An_unconfigured_line_reads_as_asking_for_nothing()
    {
        var product = await SeedAsync("CPT-001", "Compte courant", ProductCategory.CurrentAccount);

        var dto = await Facade().GetBeneficiaryPolicyAsync(
            _tenant, product.Id, CancellationToken.None);

        dto.Should().NotBeNull("the line exists; it simply collects nothing");
        dto!.HouseholdEnabled.Should().BeFalse();
        dto.AllowedRoles.Should().BeEmpty();
        dto.RequiredFieldsBeforeConversion.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dangling_line_id_reads_as_null_which_is_NOT_the_same_as_asking_for_nothing()
    {
        // The caller must be able to tell a bug (a reference pointing nowhere) from a current
        // account (a line that genuinely collects nothing).
        await SeedAsync("CPT-001", "Compte courant", ProductCategory.CurrentAccount);

        (await Facade().GetBeneficiaryPolicyAsync(_tenant, Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_empty_line_id_reads_as_null_without_a_query()
    {
        (await Facade().GetBeneficiaryPolicyAsync(_tenant, Guid.Empty, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Another_tenants_line_is_invisible()
    {
        var product = await SeedAsync("SANTE-01", "Assurance santé");

        (await Facade().GetBeneficiaryPolicyAsync(Guid.NewGuid(), product.Id, CancellationToken.None))
            .Should().BeNull();
    }

    // ── A policy survives the jsonb round-trip ────────────────────────────

    [Fact]
    public async Task A_policy_with_several_roles_round_trips_through_jsonb()
    {
        // The column is a typed jsonb document behind a value converter; a record holding lists is
        // exactly where a converter/comparer pair gets this wrong.
        var policy = BeneficiaryPolicy.Create(
            householdEnabled: true,
            allowedRoles:
            [
                new("AYANTDROITSANTE", "Ayant droit santé", 0, 10),
                new("BENEFICIAIREDECES", "Bénéficiaire décès", 1, 4, SharesMustTotal100: true),
                new("MANDATAIRE", "Mandataire", 0, 1),
            ],
            maxChildren: 10, childMaxAge: 21,
            requiredFieldsBeforeConversion:
                [PersonFieldCode.BirthDate, PersonFieldCode.Address, PersonFieldCode.PepStatus],
            requiredKycTier: "Full", integrationSystem: "Orass").Value;

        var product = await SeedAsync("SANTE-02", "Assurance santé étendue", policy: policy);

        await using var read = _factory.CreateContext();
        var stored = read.ProductSpecialities.Single(p => p.Id == product.Id);

        stored.BeneficiaryPolicy.Roles.Should().HaveCount(3);
        stored.BeneficiaryPolicy.Roles.Select(r => r.Code)
            .Should().BeEquivalentTo(["AYANTDROITSANTE", "BENEFICIAIREDECES", "MANDATAIRE"]);
        stored.BeneficiaryPolicy.RequiredFields.Should().HaveCount(3);
        stored.BeneficiaryPolicy.ChildMaxAge.Should().Be(21);
    }
}
