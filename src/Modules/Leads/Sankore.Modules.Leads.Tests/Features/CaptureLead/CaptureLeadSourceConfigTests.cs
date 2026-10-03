namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// The command-to-aggregate plumbing for <c>LeadSourceConfigId</c>. The field is server-set:
/// PullLeadSourceJob and ReplayIngestionJob put it on the command, and nothing else may — it is
/// deliberately absent from the HTTP request records, because nothing validates it and a caller
/// able to set it could inherit any source's dispatching rule and distort its cost reporting.
/// These two tests are the only coverage of that handoff.
/// </summary>
public sealed class CaptureLeadSourceConfigTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public CaptureLeadSourceConfigTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private CaptureLeadHandler Handler(Infrastructure.LeadsDbContext db)
    {
        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(c => $"idx_{c.Arg<string>()}");

        return new CaptureLeadHandler(
            db,
            NullLogger<CaptureLeadHandler>.Instance,
            TimeProvider.System,
            Substitute.For<IBus>(),
            indexer,
            Substitute.For<IEventPublisher>());
    }

    private CaptureLeadCommand Command(Guid? leadSourceConfigId) => new(
        TenantId: _tenantId,
        FullName: "Mariam Coulibaly",
        PhoneNumber: "+2250709081102",
        Source: LeadSource.Web,
        InterestedProduct: "Crédit commerçant",
        PreferredLanguage: "fr",
        Latitude: 5.33,
        Longitude: -4.03,
        PreferredAgencyId: null,
        Force: true,
        LeadSourceConfigId: leadSourceConfigId);

    [Fact]
    public async Task The_source_config_on_the_command_is_persisted_on_the_lead()
    {
        var sourceId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(sourceId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.LeadSourceConfigId.Should().Be(sourceId);
    }

    [Fact]
    public async Task A_command_without_a_source_config_leaves_it_null()
    {
        // The manual-capture and file-import case. Null is a legal, meaningful value here —
        // "this lead did not arrive through a configured source" — not a missing value to be
        // filled in with a placeholder.
        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.LeadSourceConfigId.Should().BeNull();
    }
}
