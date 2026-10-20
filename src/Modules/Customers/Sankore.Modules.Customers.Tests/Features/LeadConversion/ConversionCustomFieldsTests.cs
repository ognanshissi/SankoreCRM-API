namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// A converted lead's custom values reach its client when the client form has the same field
/// (dynamic forms, lot 6).
/// </summary>
public sealed class ConversionCustomFieldsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory = new(TenantId);
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Shared_keys_carry_over_and_lead_only_or_unacceptable_ones_do_not()
    {
        var catalog = NativeFormCatalog.For(FormEntity.Client);
        _admin.GetEntityFormAsync(TenantId, FormEntity.Client, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Client, 1, catalog.Sections,
            [
                .. catalog.Fields,
                new FormField("segment", FormFieldType.SingleChoice, "Segment", 1, "contact",
                    Options: [new FormOption("Commercant", "Commerçant")]),
                new FormField("maxBudget", FormFieldType.Integer, "Budget maximal", 2, "contact", Max: 1000),
            ]));

        await using var db = _factory.CreateContext();
        var handler = new CreateClientFromLeadHandler(
            db,
            new DuplicateProbe(db),
            LeadConversionTestDoubles.AgencyDirectory(LeadConversionTestDoubles.AgencyCode),
            LeadConversionTestDoubles.ClientNumbers(),
            TestDoubles.Settings(TenantId),
            TestDoubles.Encryptor(),
            TestDoubles.Indexer(),
            LeadConversionTestDoubles.PhoneticKeys(),
            new RecordingEventPublisher(),
            _admin);

        var result = await handler.Handle(new CreateClientFromLeadCommand(
            TenantId: TenantId, LeadId: Guid.NewGuid(), AgencyId: Guid.NewGuid(), ConvertedByUserId: Guid.NewGuid(),
            FirstName: "Awa", LastName: "Kone", LegalName: null, Gender: "Female", DateOfBirth: "1990-03-17",
            Nationality: "CI", PhoneNumber: "+2250708091811", Email: null, IdentityDocumentType: "NationalIdCard",
            IdentityDocumentNumber: "CI-555", Profession: null, PreferredLanguage: "fr", RequestedClientId: null,
            // segment: shared; leadScore: lead-only; maxBudget: out of the client field's range.
            CustomFieldsJson: """{"segment":"Commercant","leadScore":"chaud","maxBudget":5000}"""), default);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var read = _factory.CreateContext();
        var client = await read.Clients.IgnoreQueryFilters().SingleAsync(c => c.Id == result.Value.ClientId);
        var values = CustomFieldValues.Read(client.CustomFieldsJson)!;
        values.Keys.Should().BeEquivalentTo("segment");
    }
}
