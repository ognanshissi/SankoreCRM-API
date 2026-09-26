namespace Sankore.Modules.Leads.Tests.Features.Import;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Import;
using Sankore.Modules.Leads.Features.Import.ValidateImport;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

public sealed class LeadImportValidatorTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer = new Last8DigitsIndexer();

    public LeadImportValidatorTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private static ImportLeadRow Row(string phone, string? name = "Awa Ndiaye") => new()
    {
        FullName          = name,
        PhoneNumber       = phone,
        InterestedProduct = "Crédit individuel",
    };

    private async Task<ValidateLeadImportResponse> ValidateAsync(params ImportLeadRow[] rows)
    {
        await using var db = _factory.CreateContext();
        return await new LeadImportValidator(db, _indexer).ValidateAsync(
            [.. rows], new ImportDefaults(), LeadSource.FileImport, CancellationToken.None);
    }

    [Fact]
    public async Task Clean_rows_come_back_valid()
    {
        var response = await ValidateAsync(Row("+221771234567"), Row("+221770000000", "Moussa Diop"));

        response.TotalRows.Should().Be(2);
        response.ValidRows.Should().Be(2);
        response.InvalidRows.Should().Be(0);
        response.DuplicateRows.Should().Be(0);
        response.Rows.Should().OnlyContain(r => r.Errors.Count == 0);
    }

    [Fact]
    public async Task Parser_errors_are_reported_per_row_with_the_row_number()
    {
        var response = await ValidateAsync(
            Row("+221771234567"),
            new ImportLeadRow { PhoneNumber = "+221770000000" }); // no name, no product

        response.ValidRows.Should().Be(1);
        var bad = response.Rows.Single(r => !r.IsValid);
        bad.RowNumber.Should().Be(2);
        bad.Errors.Should().Contain(e => e.Contains("FullName"));
        bad.Errors.Should().Contain(e => e.Contains("InterestedProduct"));
    }

    [Fact]
    public async Task A_malformed_phone_is_rejected_with_the_capture_rule()
    {
        var response = await ValidateAsync(Row("not a phone"));

        response.Rows.Single().Errors.Should()
            .Contain(e => e.Contains("does not match the expected format"));
    }

    [Fact]
    public async Task The_same_phone_twice_in_one_file_flags_the_second_row()
    {
        // Same subscriber, written two ways — the blind index collapses both.
        var response = await ValidateAsync(Row("+221771234567"), Row("0771234567", "Awa N."));

        response.DuplicateRows.Should().Be(1);
        response.Rows[0].IsDuplicate.Should().BeFalse();
        response.Rows[1].IsDuplicate.Should().BeTrue();
        response.Rows[1].Errors.Should().Contain(e => e.Contains("first seen on row 1"));
    }

    [Fact]
    public async Task A_row_matching_an_existing_lead_is_flagged_with_that_lead_id()
    {
        var existing = await SeedLeadAsync("+221771234567");

        var response = await ValidateAsync(Row("+221771234567"));

        var row = response.Rows.Single();
        row.IsDuplicate.Should().BeTrue();
        row.IsValid.Should().BeFalse();
        row.DuplicateOfLeadId.Should().Be(existing.Id);
        row.Errors.Should().Contain(e => e.Contains("would be skipped"));
    }

    [Fact]
    public async Task A_closed_lead_does_not_block_the_row()
    {
        var existing = await SeedLeadAsync("+221771234567");

        await using (var db = _factory.CreateContext())
        {
            var lead = db.Leads.AsTracking().Single(l => l.Id == existing.Id);
            lead.Close(LeadCloseReason.Lost, "no answer");
            await db.SaveChangesAsync();
        }

        var response = await ValidateAsync(Row("+221771234567"));

        response.Rows.Single().IsDuplicate.Should().BeFalse();
        response.ValidRows.Should().Be(1);
    }

    private async Task<Lead> SeedLeadAsync(string phone)
    {
        var lead = Lead.Capture(
            tenantId:          _tenantId,
            fullName:          "Awa Ndiaye",
            phoneNumber:       phone,
            source:            LeadSource.Web,
            interestedProduct: "Crédit individuel",
            preferredLanguage: "FR",
            location:          new GeoPoint(14.6928, -17.4467),
            preferredAgencyId: null,
            clock:             TimeProvider.System,
            phoneBlindIndex:   _indexer.Compute(phone));

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        return lead;
    }

    /// <summary>Mirrors the production indexer's normalization (last 8 digits) without the HMAC.</summary>
    private sealed class Last8DigitsIndexer : IPhoneBlindIndexer
    {
        public string Compute(string phoneNumber)
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            return digits.Length > 8 ? digits[^8..] : digits;
        }
    }
}
