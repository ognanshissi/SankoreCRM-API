namespace Sankore.Modules.Leads.Tests.Features.Consents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Features.RecordConsent;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Les consentements qu'une ligne de produit exige avant la conversion, et la façon dont l'agent
/// les recueille : accord ou refus, avec l'appel qui le prouve.
/// </summary>
public sealed class RequiredConsentTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public RequiredConsentTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.FatcaOff);
        _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
              .Returns(new BeneficiaryPolicyDto(
                  ProductLineId: _line, ProductCode: "CREDIT-EQ", Category: nameof(ProductCategory.Loan),
                  IsActive: true, AllowMultipleHoldings: false, AllowedRoles: [],
                  MaxChildren: null, ChildMaxAge: null,
                  RequiredFieldsBeforeConversion: [PersonFieldCode.ConsentDataProcessing, PersonFieldCode.ConsentCreditBureau],
                  RequiredKycTier: null, IntegrationSystem: null));
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedAsync()
    {
        // The lead stores no product line: its asked product resolves to it.
        _admin.ResolveProductLineAsync(_tenant, "Crédit équipement", Arg.Any<CancellationToken>()).Returns(_line);
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Didier Gbagbo", phoneNumber: "+2250701020324",
            source: LeadSource.CallCenter, interestedProduct: "Crédit équipement",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null, clock: TimeProvider.System,
            firstName: "Didier", lastName: "Gbagbo", ownerId: _agent);
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);
        return lead;
    }

    private async Task<LeadCompletenessDto> ComputeAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        var lead = await db.Leads.AsTracking().Include(l => l.HouseholdMembers)
            .FirstAsync(l => l.Id == leadId, CancellationToken.None);
        return await TestCompleteness.Calculator(db, _admin).ComputeAsync(lead, CancellationToken.None);
    }

    private async Task<Result<RecordConsentResult>> RecordAsync(
        Guid leadId, ConsentType type, bool granted = true, Guid? proofActivityId = null)
    {
        await using var db = _factory.CreateContext();
        return await new RecordConsentHandler(db, TimeProvider.System).Handle(
            new RecordConsentCommand(_tenant, leadId, type, ConsentChannel.Phone, _agent,
                Granted: granted, ProofActivityId: proofActivityId),
            CancellationToken.None);
    }

    [Fact]
    public async Task Each_required_consent_not_given_blocks_the_conversion()
    {
        var lead = await SeedAsync();

        var report = await ComputeAsync(lead.Id);

        report.CanConvert.Should().BeFalse();
        report.Gaps.Where(g => g.Code == CompletenessCodes.ConsentMissing).Select(g => g.Detail)
            .Should().BeEquivalentTo([PersonFieldCode.ConsentDataProcessing, PersonFieldCode.ConsentCreditBureau]);
        report.RequiredConsents.Should().BeEquivalentTo(
            [nameof(ConsentType.DataProcessing), nameof(ConsentType.CreditBureauCheck)]);
    }

    [Fact]
    public async Task Given_consents_close_the_gaps_and_stay_listed_as_required()
    {
        var lead = await SeedAsync();
        (await RecordAsync(lead.Id, ConsentType.DataProcessing)).IsSuccess.Should().BeTrue();
        (await RecordAsync(lead.Id, ConsentType.CreditBureauCheck)).IsSuccess.Should().BeTrue();

        var report = await ComputeAsync(lead.Id);

        report.Gaps.Should().NotContain(g => g.Code == CompletenessCodes.ConsentMissing);
        report.CanConvert.Should().BeTrue();
        report.RequiredConsents.Should().HaveCount(2, "the screen keeps saying « obligatoire » once given");
    }

    [Fact]
    public async Task A_refusal_is_recorded_and_does_not_satisfy_the_requirement()
    {
        var lead = await SeedAsync();

        var refused = await RecordAsync(lead.Id, ConsentType.CreditBureauCheck, granted: false);

        refused.IsSuccess.Should().BeTrue(refused.Error);
        await using (var db = _factory.CreateContext())
        {
            (await db.LeadConsents.SingleAsync(CancellationToken.None)).Status.Should().Be(ConsentStatus.Refused);
        }
        (await ComputeAsync(lead.Id)).Gaps
            .Should().Contain(g => g.Code == CompletenessCodes.ConsentMissing && g.Detail == PersonFieldCode.ConsentCreditBureau);
    }

    [Fact]
    public async Task A_refusal_over_a_standing_agreement_must_go_through_a_withdrawal()
    {
        var lead = await SeedAsync();
        await RecordAsync(lead.Id, ConsentType.DataProcessing);

        var refused = await RecordAsync(lead.Id, ConsentType.DataProcessing, granted: false);

        refused.IsFailure.Should().BeTrue();
        refused.Error.Should().Be(RecordConsentHandler.WithdrawFirst);
    }

    [Fact]
    public async Task The_proof_call_must_belong_to_the_lead()
    {
        var lead = await SeedAsync();
        Guid callId;
        await using (var db = _factory.CreateContext())
        {
            var call = LeadActivity.Create(_tenant, lead.Id, ActivityType.Call, "Appel sortant", _agent);
            db.LeadActivities.Add(call);
            await db.SaveChangesAsync(CancellationToken.None);
            callId = call.Id;
        }

        (await RecordAsync(lead.Id, ConsentType.DataProcessing, proofActivityId: Guid.NewGuid()))
            .Error.Should().Be(RecordConsentHandler.ActivityNotFound);

        var recorded = await RecordAsync(lead.Id, ConsentType.DataProcessing, proofActivityId: callId);
        recorded.IsSuccess.Should().BeTrue(recorded.Error);
        await using var check = _factory.CreateContext();
        (await check.LeadConsents.SingleAsync(CancellationToken.None)).ProofActivityId.Should().Be(callId);
    }

    [Fact]
    public void A_refusal_cannot_be_withdrawn()
    {
        var refusal = LeadConsent.Create(_tenant, Guid.NewGuid(), ConsentType.Marketing, ConsentChannel.Sms,
            _agent, TimeProvider.System, granted: false);

        refusal.Withdraw(_agent, TimeProvider.System).Error.Should().Be("CONSENT_REFUSED_NOT_WITHDRAWABLE");
    }

    [Fact]
    public void The_consent_codes_are_known_to_the_policy_validation()
    {
        PersonFieldCode.IsKnown(PersonFieldCode.ConsentCreditBureau).Should().BeTrue();
        PersonFieldCode.ScopeOf(PersonFieldCode.ConsentDataProcessing).Should().Be(PersonFieldScope.PrincipalOnly);
    }
}
