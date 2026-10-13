namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers the half of KRM-B-01 the value object cannot: the two refusals that depend on the
/// tenant's compliance row, which the handler is the only layer able to read.
///
/// <para>
/// A tenant with NO row is the interesting case and the common one — an institution that has never
/// opened the compliance screen. It must behave exactly as one that opened it and saved without
/// changing anything: machine approval off. Reading an absent row as permissive is how automatic
/// approval would arrive by deployment rather than by decision.
/// </para>
/// </summary>
public sealed class SetKycRemotePolicyHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public SetKycRemotePolicyHandlerTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<ProductSpeciality> SeedProductAsync()
    {
        var product = ProductSpeciality.Create(
            _tenant, "Assurance santé", "SANTE-01", ProductCategory.Insurance);

        await using var db = _factory.CreateContext();
        db.ProductSpecialities.Add(product);
        await db.SaveChangesAsync(CancellationToken.None);
        return product;
    }

    private async Task SeedComplianceAsync(bool autoEnabled, string maxTier)
    {
        var settings = ComplianceSettings.CreateDefault(
            _tenant, Guid.NewGuid(), DateTimeOffset.UtcNow);

        settings.ConfigureKycAutoApproval(
            autoEnabled, maxTier, samplePct: 10, actor: Guid.NewGuid(),
            now: DateTimeOffset.UtcNow);

        await using var db = _factory.CreateContext();
        db.ComplianceSettings.Add(settings);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<Result> HandleAsync(SetKycRemotePolicyCommand cmd)
    {
        await using var db = _factory.CreateContext();
        return await new SetKycRemotePolicyHandler(db, new ProductCatalogCache())
            .Handle(cmd, CancellationToken.None);
    }

    private async Task<KycRemotePolicy> ReadPolicyAsync(Guid productId)
    {
        await using var db = _factory.CreateContext();
        var product = await db.ProductSpecialities.FindAsync([productId], CancellationToken.None);
        return product!.KycRemotePolicy;
    }

    [Fact]
    public async Task An_unknown_product_is_a_404_not_a_400()
    {
        var result = await HandleAsync(new SetKycRemotePolicyCommand(ProductId: Guid.NewGuid()));

        result.Error.Should().Be("PRODUCT_NOT_FOUND");
    }

    [Fact]
    public async Task A_manual_policy_is_saved_as_given()
    {
        var product = await SeedProductAsync();

        var result = await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.ManualMode,
            RequiredTier: KycRemotePolicyValues.FullTier,
            RequiredDocuments: ["ProofOfAddress"],
            AllowedDocumentTypes: ["Cni", "Passport"],
            LinkValidityHours: 48));

        result.IsSuccess.Should().BeTrue();

        var policy = await ReadPolicyAsync(product.Id);
        policy.Enabled.Should().BeTrue();
        policy.RequiredTier.Should().Be(KycRemotePolicyValues.FullTier);
        policy.Documents.Should().Equal("ProofOfAddress");
        policy.DocumentTypes.Should().Equal("Cni", "Passport");
        policy.LinkValidityHours.Should().Be(48);
    }

    [Fact]
    public async Task Automatic_is_refused_on_a_tenant_that_has_never_opened_the_compliance_screen()
    {
        // No compliance row at all — the platform defaults apply, and they say no.
        var product = await SeedProductAsync();

        var result = await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            AllowedDocumentTypes: ["Cni"]));

        result.Error.Should().Be("KYC_REMOTE_AUTO_APPROVAL_DISABLED");
    }

    [Fact]
    public async Task A_refusal_leaves_the_product_untouched()
    {
        // Including the enabled flag, which is part of the same decision: a line left "remote
        // collection on, validation unclear" is worse than one left alone.
        var product = await SeedProductAsync();

        await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            AllowedDocumentTypes: ["Cni"]));

        (await ReadPolicyAsync(product.Id)).Should().Be(KycRemotePolicy.Disabled);
    }

    [Fact]
    public async Task Automatic_is_accepted_once_the_tenant_enables_machine_approval()
    {
        await SeedComplianceAsync(autoEnabled: true, KycRemotePolicyValues.SimplifiedTier);
        var product = await SeedProductAsync();

        var result = await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            RequiredTier: KycRemotePolicyValues.SimplifiedTier,
            MinAutoScore: 90,
            AllowedDocumentTypes: ["Cni"]));

        result.IsSuccess.Should().BeTrue();

        var policy = await ReadPolicyAsync(product.Id);
        policy.WantsAutomaticValidation.Should().BeTrue();
        policy.MinAutoScore.Should().Be(90);
    }

    [Fact]
    public async Task Automatic_above_the_tenant_ceiling_is_refused_even_when_enabled()
    {
        await SeedComplianceAsync(autoEnabled: true, KycRemotePolicyValues.SimplifiedTier);
        var product = await SeedProductAsync();

        var result = await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            RequiredTier: KycRemotePolicyValues.FullTier,
            AllowedDocumentTypes: ["Cni"]));

        result.Error.Should().Be("KYC_REMOTE_TIER_ABOVE_AUTO_LIMIT");
    }

    [Fact]
    public async Task Another_tenants_permission_is_not_borrowed()
    {
        // The compliance read is scoped to the PRODUCT's tenant, not to the ambient one. Seeding
        // an enabling row under a different tenant must change nothing here.
        var otherTenant = Guid.NewGuid();
        var settings = ComplianceSettings.CreateDefault(
            otherTenant, Guid.NewGuid(), DateTimeOffset.UtcNow);
        settings.ConfigureKycAutoApproval(
            true, KycRemotePolicyValues.FullTier, 10, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await using (var seed = _factory.CreateContext())
        {
            seed.ComplianceSettings.Add(settings);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var product = await SeedProductAsync();

        var result = await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            AllowedDocumentTypes: ["Cni"]));

        result.Error.Should().Be("KYC_REMOTE_AUTO_APPROVAL_DISABLED");
    }

    [Fact]
    public async Task Turning_the_tenant_switch_back_off_does_not_rewrite_the_product()
    {
        // Documented behaviour: the product keeps saying what the institution once wanted, and the
        // gate in M02 simply stops honouring it. That is what lets an officer disable the feature
        // in one click without losing a catalogue's worth of configuration.
        await SeedComplianceAsync(autoEnabled: true, KycRemotePolicyValues.SimplifiedTier);
        var product = await SeedProductAsync();

        await HandleAsync(new SetKycRemotePolicyCommand(
            ProductId: product.Id,
            Enabled: true,
            ValidationMode: KycRemotePolicyValues.AutomaticMode,
            AllowedDocumentTypes: ["Cni"]));

        await using (var db = _factory.CreateContext())
        {
            var settings = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstAsync(db.ComplianceSettings, CancellationToken.None);
            settings.ConfigureKycAutoApproval(
                false, KycRemotePolicyValues.SimplifiedTier, 10, Guid.NewGuid(),
                DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await ReadPolicyAsync(product.Id)).WantsAutomaticValidation.Should().BeTrue();
    }
}
