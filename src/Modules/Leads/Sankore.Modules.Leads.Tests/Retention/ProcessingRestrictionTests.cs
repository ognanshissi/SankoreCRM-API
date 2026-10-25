namespace Sankore.Modules.Leads.Tests.Retention;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Xunit;

/// <summary>
/// A person who asked to be forgotten and waits for the end of their legal retention (data-purge
/// lot 3): no new project, no WhatsApp contact we initiate.
/// </summary>
public sealed class ProcessingRestrictionTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public ProcessingRestrictionTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _customers.IsProcessingRestrictedAsync(_tenant, _clientId, Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task No_project_is_opened_for_a_restricted_client()
    {
        await using var db = _factory.CreateContext();
        var result = await new CreateOpportunityForCustomerHandler(
                db, TimeProvider.System, Substitute.For<IPhoneBlindIndexer>(), customers: _customers)
            .Handle(new CreateOpportunityForCustomerCommand(_tenant, _clientId, "Crédit habitat", "CREDIT_HABITAT"), default);

        result.Error.Should().Be(ProcessingRestriction.Error);
    }

    [Fact]
    public void A_restriction_blocks_a_template_before_anything_else_even_forced()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();

        var blockers = WhatsAppEligibility.Blockers(
            lead, WhatsAppConsentState.Granted, "+2250700000000", channelActive: true,
            conversation: null, mayForce: true, DateTimeOffset.UtcNow, processingRestricted: true);

        blockers.Should().StartWith(WhatsAppErrors.ProcessingRestricted);
    }
}
