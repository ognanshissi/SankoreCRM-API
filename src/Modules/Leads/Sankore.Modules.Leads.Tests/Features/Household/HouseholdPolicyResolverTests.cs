namespace Sankore.Modules.Leads.Tests.Features.Household;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <see cref="HouseholdPolicyResolver"/> — the product line's say over the household
/// (BNF-B-03, BNF-B-06).
/// </summary>
public sealed class HouseholdPolicyResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    private HouseholdPolicyResolver Resolver() => new(_admin);

    private static Lead LeadWith(Guid? productLineId) => Lead.Capture(
        tenantId: Tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
        source: LeadSource.Agency, interestedProduct: "Assurance santé",
        preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
        clock: TimeProvider.System, ownerId: Guid.NewGuid(), productLineId: productLineId);

    private void PolicyFor(
        Guid lineId, bool householdEnabled, int? maxChildren = null, int? childMaxAge = null) =>
        _admin.GetBeneficiaryPolicyAsync(Tenant, lineId, Arg.Any<CancellationToken>())
            .Returns(new BeneficiaryPolicyDto(
                ProductLineId: lineId, ProductCode: "SANTE-01", Category: "Insurance",
                IsActive: true, HouseholdEnabled: householdEnabled, AllowMultipleHoldings: false,
                AllowedRoles: [], MaxChildren: maxChildren, ChildMaxAge: childMaxAge,
                RequiredFieldsBeforeConversion: [], RequiredKycTier: null, IntegrationSystem: null));

    [Fact]
    public async Task An_unresolved_product_line_is_allowed_with_no_limits()
    {
        // The documented position: an agent collects the household while the prospect is in front
        // of them and qualifies the request afterwards. The gap is reported by the completeness
        // check, and the conversion refuses on it (BNF-B-04).
        var result = await Resolver().ResolveAsync(LeadWith(null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(HouseholdLimits.Unlimited);

        await _admin.DidNotReceive().GetBeneficiaryPolicyAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_line_that_records_no_household_refuses()
    {
        var line = Guid.NewGuid();
        PolicyFor(line, householdEnabled: false);

        var result = await Resolver().ResolveAsync(LeadWith(line), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HouseholdPolicyResolver.Disabled);
    }

    [Fact]
    public async Task A_line_that_records_a_household_returns_its_limits()
    {
        var line = Guid.NewGuid();
        PolicyFor(line, householdEnabled: true, maxChildren: 10, childMaxAge: 21);

        var result = await Resolver().ResolveAsync(LeadWith(line), CancellationToken.None);

        result.Value.MaxChildren.Should().Be(10);
        result.Value.ChildMaxAge.Should().Be(21);
    }

    [Fact]
    public async Task A_dangling_line_id_is_allowed_rather_than_refused()
    {
        // The product was deleted, or the id was never valid. BNF-B-01 makes the reference opaque
        // precisely so a retired product degrades gracefully; refusing would strand a lead whose
        // product line somebody else removed.
        var line = Guid.NewGuid();
        _admin.GetBeneficiaryPolicyAsync(Tenant, line, Arg.Any<CancellationToken>())
            .Returns((BeneficiaryPolicyDto?)null);

        var result = await Resolver().ResolveAsync(LeadWith(line), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(HouseholdLimits.Unlimited);
    }

    [Fact]
    public async Task An_M12_outage_FAILS_the_write()
    {
        // The asymmetry with the capture path is deliberate: a capture must never lose a prospect
        // on the phone, whereas a household edit is a deliberate action the agent repeats — and
        // letting it through would bypass the child ceiling an institution configured.
        var line = Guid.NewGuid();
        _admin.GetBeneficiaryPolicyAsync(Tenant, line, Arg.Any<CancellationToken>())
            .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new HttpRequestException("M12 down"));

        var result = await Resolver().ResolveAsync(LeadWith(line), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HouseholdPolicyResolver.PolicyUnavailable);
    }
}
