namespace Sankore.Modules.Leads.Tests.Features.Ingestions.ReprocessIngestion;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.Ingestions.ReprocessIngestion;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

public sealed class ReprocessIngestionHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer;

    // What a web form whose field names are not lead field names posts: the pipeline stores it
    // as REQUIRED_FIELDS_MISSING, and nothing but an operator can say which key is the phone.
    private const string FrenchFormPayload =
        """{"telephone":" +2250707070707 ","nom":"Awa Koné","_page":"https://exemple.ci/contact"}""";

    public ReprocessIngestionHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer = Substitute.For<IPhoneBlindIndexer>();
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(LeadSourceConfig Source, LeadIngestion Ingestion)> SeedFailedIngestion(
        string payload = FrenchFormPayload)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Site", LeadChannelType.WalkIn, 0);
        source.StartTesting();
        source.Activate();
        db.LeadSourceConfigs.Add(source);

        var ingestion = LeadIngestion.CreateFailed(
            _tenantId, source.Id, "REQUIRED_FIELDS_MISSING:phoneNumber,fullName|firstName|lastName",
            TimeProvider.System, rawPayloadJson: payload, externalId: "ext-1");
        db.LeadIngestions.Add(ingestion);
        await db.SaveChangesAsync();
        return (source, ingestion);
    }

    private async Task<Shared.Kernel.Result<ReprocessIngestionResult>> Reprocess(
        Guid ingestionId, Dictionary<string, string> links)
    {
        await using var db = _factory.CreateContext();
        var handler = new ReprocessIngestionHandler(db, _indexer, TimeProvider.System);
        return await handler.Handle(new ReprocessIngestionCommand(ingestionId, links), CancellationToken.None);
    }

    [Fact]
    public async Task Linked_fields_create_the_lead_and_complete_the_same_ingestion_row()
    {
        var (source, ingestion) = await SeedFailedIngestion();

        var result = await Reprocess(ingestion.Id, new()
        {
            ["phoneNumber"] = "telephone",
            ["fullName"] = "nom",
        });

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Status.Should().Be(LeadIngestionStatus.Accepted);

        await using var verify = _factory.CreateContext();
        var row = verify.LeadIngestions.Single(i => i.Id == ingestion.Id);
        row.Status.Should().Be(LeadIngestionStatus.Accepted);
        row.LeadId.Should().Be(result.Value.LeadId);
        row.RejectionReason.Should().BeNull();
        verify.LeadIngestions.Count().Should().Be(1, "the row is completed, not duplicated");

        var lead = verify.Leads.Single(l => l.Id == result.Value.LeadId);
        lead.FullName.Should().Be("Awa Koné");
        lead.PhoneNumber.Should().Be("+2250707070707");
        lead.LeadSourceConfigId.Should().Be(source.Id);
    }

    [Fact]
    public async Task Still_missing_a_required_field_refuses_and_leaves_the_row_untouched()
    {
        var (_, ingestion) = await SeedFailedIngestion();

        var result = await Reprocess(ingestion.Id, new() { ["fullName"] = "nom" });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("REQUIRED_FIELDS_MISSING:phoneNumber");

        await using var verify = _factory.CreateContext();
        verify.LeadIngestions.Single().Status.Should().Be(LeadIngestionStatus.Failed);
        verify.Leads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_absent_from_the_payload_is_refused_by_name()
    {
        var (_, ingestion) = await SeedFailedIngestion();

        var result = await Reprocess(ingestion.Id, new()
        {
            ["phoneNumber"] = "mobile",
            ["fullName"] = "nom",
        });

        result.Error.Should().Be("PAYLOAD_FIELD_NOT_FOUND:mobile");
    }

    [Fact]
    public async Task An_existing_open_lead_with_the_phone_marks_the_row_duplicate_of_it()
    {
        var (_, ingestion) = await SeedFailedIngestion();
        var first = await Reprocess(ingestion.Id, new()
        {
            ["phoneNumber"] = "telephone",
            ["fullName"] = "nom",
        });

        var (_, second) = await SeedFailedIngestion(
            """{"tel":"+2250707070707","nom":"Awa K."}""");
        var result = await Reprocess(second.Id, new()
        {
            ["phoneNumber"] = "tel",
            ["fullName"] = "nom",
        });

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Status.Should().Be(LeadIngestionStatus.Duplicate);
        result.Value.LeadId.Should().Be(first.Value.LeadId);

        await using var verify = _factory.CreateContext();
        verify.Leads.Count().Should().Be(1);
        verify.LeadIngestions.Single(i => i.Id == second.Id).LeadId.Should().Be(first.Value.LeadId);
    }

    [Fact]
    public async Task An_accepted_ingestion_cannot_be_reprocessed()
    {
        var (_, ingestion) = await SeedFailedIngestion();
        await Reprocess(ingestion.Id, new() { ["phoneNumber"] = "telephone", ["fullName"] = "nom" });

        var again = await Reprocess(ingestion.Id, new() { ["phoneNumber"] = "telephone", ["fullName"] = "nom" });

        again.Error.Should().Be("ONLY_REJECTED_OR_FAILED_CAN_BE_REPROCESSED");
    }

    [Theory]
    [InlineData("gender")]        // a valid mapping target the inbound lead does not carry
    [InlineData("dateOfBirth")]
    [InlineData("nope")]
    public void A_target_the_lead_is_not_built_from_is_refused(string target)
    {
        var result = new ReprocessIngestionValidator().Validate(
            new ReprocessIngestionCommand(Guid.NewGuid(), new Dictionary<string, string> { [target] = "x" }));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Every_captured_field_is_accepted()
    {
        var links = InboundLeadFactory.CapturedFields.ToDictionary(f => f, _ => "key");

        new ReprocessIngestionValidator()
            .Validate(new ReprocessIngestionCommand(Guid.NewGuid(), links))
            .IsValid.Should().BeTrue();
    }
}
