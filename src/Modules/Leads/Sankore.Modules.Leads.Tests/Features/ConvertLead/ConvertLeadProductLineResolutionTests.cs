namespace Sankore.Modules.Leads.Tests.Features.ConvertLead;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A lead reaching conversion without a product line — built by the ingestion pipeline, or whose
/// label an agent corrected — used to convert with none, so its KYC file was opened with no
/// product and fell back to the default remote policy. The conversion now looks the label up
/// itself when the line is missing.
/// </summary>
public sealed class ConvertLeadProductLineResolutionTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ConvertLeadProductLineResolutionTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _administration.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TestCompleteness.FatcaOff);
        _administration.ResolveProductLineAsync(_tenantId, "Crédit commerçant", Arg.Any<CancellationToken>())
            .Returns(_line);
        _customers.CreateFromLeadAsync(Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new CreateFromLeadResult(Guid.NewGuid(), "N-1", AlreadyExisted: false)));
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedAsync(string product, Guid? productLineId = null)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithAgency(Guid.NewGuid()).WithProduct(product).Build();
        if (productLineId is not null) lead.SetProductLine(productLineId);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task ConvertAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.TenantId.Returns(_tenantId);

        var handler = new ConvertLeadHandler(
            db, user, _customers, _administration, TestLeadEmailIndexer.Instance,
            TestCompleteness.Calculator(db, _administration, _customers), _publisher);

        var result = await handler.Handle(new ConvertLeadCommand(leadId, Force: true), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    private KycRequestedIntegrationEvent PublishedKycRequest()
        => _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<KycRequestedIntegrationEvent>()
            .Single();

    [Fact]
    public async Task A_missing_line_is_resolved_from_the_label_and_reaches_the_kyc_request()
    {
        var lead = await SeedAsync("Crédit commerçant");

        await ConvertAsync(lead.Id);

        PublishedKycRequest().ProductLineId.Should().Be(_line);

        await using var verify = _factory.CreateContext();
        verify.Leads.Single(l => l.Id == lead.Id).ProductLineId.Should().Be(_line);
    }

    [Fact]
    public async Task A_line_already_on_the_lead_is_kept_and_not_looked_up_again()
    {
        var chosen = Guid.NewGuid();
        var lead = await SeedAsync("Crédit commerçant", chosen);

        await ConvertAsync(lead.Id);

        PublishedKycRequest().ProductLineId.Should().Be(chosen);
        await _administration.DidNotReceive()
            .ResolveProductLineAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_label_matching_no_line_still_converts_without_one()
    {
        var lead = await SeedAsync("Produit inconnu au catalogue");

        await ConvertAsync(lead.Id);

        PublishedKycRequest().ProductLineId.Should().BeNull();
    }
}
