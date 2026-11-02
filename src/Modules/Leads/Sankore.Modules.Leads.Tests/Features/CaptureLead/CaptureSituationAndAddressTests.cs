namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// The creation form asks for the profession, the marital status and the address (product owner,
/// 2026-11-02): they land in the columns the identity screen edits, and an unknown status refuses
/// the capture instead of being dropped.
/// </summary>
public sealed class CaptureSituationAndAddressTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public CaptureSituationAndAddressTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private CaptureLeadHandler Handler(Infrastructure.LeadsDbContext db)
    {
        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(c => $"idx_{c.Arg<string>()}");
        return new CaptureLeadHandler(
            db, NullLogger<CaptureLeadHandler>.Instance, TimeProvider.System, Substitute.For<IBus>(), indexer,
            TestLeadEmailIndexer.Instance,
            Substitute.For<Sankore.Modules.Customers.PublicApi.ICustomersModule>(),
            Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>(),
            Substitute.For<IEventPublisher>());
    }

    private CaptureLeadCommand Command(string? maritalStatus) => new(
        TenantId: _tenantId,
        FullName: "Mariam Coulibaly",
        PhoneNumber: "+2250709081102",
        Source: LeadSource.Agency,
        InterestedProduct: "Crédit commerçant",
        PreferredLanguage: "fr",
        Latitude: 5.33,
        Longitude: -4.03,
        PreferredAgencyId: null,
        Force: true,
        Profession: "  Commerçante ",
        MaritalStatus: maritalStatus,
        AddressLine: "Rue des Jardins, lot 12",
        PostalCode: "01 BP 1234",
        City: "Abidjan",
        CountryCode: "ci");

    [Fact]
    public async Task The_situation_and_the_address_are_stored_on_the_lead()
    {
        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command("married"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);

        await using var verify = _factory.CreateContext();
        var lead = await verify.Leads.SingleAsync(l => l.Id == result.Value.LeadId);

        lead.Profession.Should().Be("Commerçante");
        lead.MaritalStatus.Should().Be(LeadMaritalStatus.Married);
        lead.PrincipalAddressLine.Should().Be("Rue des Jardins, lot 12");
        lead.PrincipalPostalCode.Should().Be("01 BP 1234");
        lead.PrincipalCity.Should().Be("Abidjan");
        lead.PrincipalCountryCode.Should().Be("CI");
    }

    [Fact]
    public async Task An_unknown_marital_status_refuses_the_capture()
    {
        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command("Fiancé"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_MARITAL_STATUS_INVALID");
        (await db.Leads.CountAsync()).Should().Be(0);
    }
}
