namespace Sankore.Modules.Leads.Tests.Features.Enrichment;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// A lead that belongs to a known customer (BNF-B-23): nothing of their identity is re-entered,
/// and what is checked instead is that the CLIENT FILE is up to date — with every blocking gap
/// pointing at the action to take on that file.
/// </summary>
public sealed class ExistingCustomerCompletenessTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();

    public ExistingCustomerCompletenessTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.FatcaOff);
        Policy();
        ClientIs("Active");
        _kyc.GetStatusAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns(KycStatus.Approved);
    }

    public void Dispose() => _factory.Dispose();

    private void Policy(string? requiredKycTier = null, params string[] fields)
        => _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
                 .Returns(new BeneficiaryPolicyDto(
                     _line, "SANTE-01", nameof(ProductCategory.HealthInsurance),
                     true, true, false, [], null, null, fields, requiredKycTier, null));

    private void ClientIs(string status)
        => _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
                     .Returns(new ClientSummary(
                         _customer, "ABJ-PLT-2026-000042", "Individual", "Aya Kouassi",
                         status, Guid.NewGuid(), null, "Approved", "Low", null));

    private async Task<LeadCompletenessDto> ComputeAsync(bool attach = true)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: Guid.NewGuid(), clock: TimeProvider.System,
            ownerId: Guid.NewGuid(), productLineId: _line);

        if (attach)
            lead.AttachToCustomer(_customer, isAutomatic: false, reason: "Confirmed by the agent");

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);

        return await TestCompleteness.Calculator(db, _admin, _customers, _kyc)
            .ComputeAsync(tracked, CancellationToken.None);
    }

    // ── no re-entry ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_principals_identity_is_never_asked_for_again()
    {
        // The lead carries no first or last name at all. For a NEW prospect that would be two
        // gaps; for an attached one the client record is the source of truth and the leads schema
        // deliberately holds no copy.
        Policy(null, PersonFieldCode.FirstName, PersonFieldCode.LastName, PersonFieldCode.BirthDate);

        var report = await ComputeAsync();

        report.Kind.Should().Be(nameof(LeadKind.ExistingCustomer));
        report.Gaps.Should().NotContain(g => g.Code == CompletenessCodes.MissingRequiredField);
        report.CanConvert.Should().BeTrue();
    }

    [Fact]
    public async Task The_same_lead_unattached_DOES_have_to_supply_them()
    {
        // The contrast is the point: the fields are required either way, and attaching the lead is
        // what moves responsibility for them to M01.
        Policy(null, PersonFieldCode.FirstName, PersonFieldCode.LastName);

        var report = await ComputeAsync(attach: false);

        report.Gaps.Should().HaveCount(2);
        report.Gaps.Should().OnlyContain(g =>
            g.Code == CompletenessCodes.MissingRequiredField && g.SubjectLabel == "Le titulaire");
    }

    // ── the client file must be up to date ──────────────────────────────────

    [Theory]
    [InlineData(KycStatus.NotStarted, CompletenessCodes.CustomerKycNotApproved)]
    [InlineData(KycStatus.Pending,    CompletenessCodes.CustomerKycNotApproved)]
    [InlineData(KycStatus.InProgress, CompletenessCodes.CustomerKycNotApproved)]
    [InlineData(KycStatus.Expired,    CompletenessCodes.CustomerKycExpired)]
    [InlineData(KycStatus.Rejected,   CompletenessCodes.CustomerKycRejected)]
    public async Task A_KYC_that_is_not_approved_blocks_with_its_own_code(
        KycStatus status, string expected)
    {
        _kyc.GetStatusAsync(_tenant, _customer, Arg.Any<CancellationToken>()).Returns(status);

        var report = await ComputeAsync();

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().ContainSingle()
              .Which.Should().Match<CompletenessGap>(g =>
                  g.Code == expected && g.ActionHint == "FIX_ON_CUSTOMER");
    }

    [Fact]
    public async Task A_customer_with_no_KYC_file_at_all_blocks()
    {
        _kyc.GetStatusAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns((KycStatus?)null);

        var report = await ComputeAsync();

        report.Gaps.Should().ContainSingle()
              .Which.Detail.Should().Be("NoFile", "null means no file, never 'approved'");
    }

    [Theory]
    [InlineData("Archived")]
    [InlineData("Suspended")]
    [InlineData("Merged")]
    public async Task An_inactive_client_is_not_somewhere_to_sell(string status)
    {
        ClientIs(status);

        var report = await ComputeAsync();

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().Contain(g =>
            g.Code == CompletenessCodes.CustomerNotActive && g.Detail == status);
    }

    [Fact]
    public async Task A_lead_naming_a_client_M01_does_not_have_blocks()
    {
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
                  .Returns((ClientSummary?)null);

        var report = await ComputeAsync();

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().ContainSingle()
              .Which.Code.Should().Be(CompletenessCodes.CustomerNotFound);
    }

    // ── the tier the product line requires ──────────────────────────────────

    [Fact]
    public async Task A_line_requiring_the_full_tier_refuses_a_simplified_customer()
    {
        Policy(requiredKycTier: "Full");
        _kyc.GetLimitsAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Simplified", true, 500_000m, 1_000_000m, 30, 80));

        var report = await ComputeAsync();

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().ContainSingle()
              .Which.Should().Match<CompletenessGap>(g =>
                  g.Code == CompletenessCodes.CustomerKycTierInsufficient
                  && g.Detail == "Full" && g.ActionHint == "FIX_ON_CUSTOMER");
    }

    [Fact]
    public async Task A_customer_on_the_required_tier_passes()
    {
        Policy(requiredKycTier: "Full");
        _kyc.GetLimitsAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Full", false, 0m, 0m, 0, 0));

        var report = await ComputeAsync();

        report.Gaps.Should().BeEmpty();
        report.CanConvert.Should().BeTrue();
    }

    [Fact]
    public async Task A_line_requiring_no_tier_never_asks_M02_for_the_limits()
    {
        var report = await ComputeAsync();

        report.CanConvert.Should().BeTrue();
        await _kyc.DidNotReceive().GetLimitsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── an outage never reads as complete ───────────────────────────────────

    [Fact]
    public async Task An_unreachable_client_module_blocks_instead_of_passing()
    {
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
                  .Returns<Task<ClientSummary?>>(_ => throw new InvalidOperationException("M01 down"));

        var report = await ComputeAsync();

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().ContainSingle()
              .Which.Should().Match<CompletenessGap>(g =>
                  g.Code == CompletenessCodes.CheckUnavailable && g.Detail == "CUSTOMER_FILE");
    }
}
