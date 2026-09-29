namespace Sankore.Modules.Customers.Tests.Features.Import;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Import;
using Sankore.Modules.Customers.Features.Import.ValidateImport;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// The validator is the whole point of the dry run: an operator importing four hundred clients
/// has to learn about the bad rows BEFORE the run, not from a failure report afterwards.
/// </summary>
public sealed class ClientImportValidatorTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public ClientImportValidatorTests() => _factory = new TestCustomersDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private ClientImportValidator Validator() =>
        new(_factory.CreateContext(), TestDoubles.Indexer(), TestDoubles.Settings(_tenantId));

    private static ImportClientRow Individual(
        string? document = "CI0001",
        string? dateOfBirth = "1990-04-02",
        string? first = "Awa",
        string? last = "Ouattara",
        string? nationality = "CI",
        string? phone = "+2250708091801") =>
        new()
        {
            FirstName = first,
            LastName = last,
            DateOfBirth = dateOfBirth,
            Nationality = nationality,
            IdentityDocumentNumber = document,
            PhoneNumber = phone,
        };

    private async Task<ValidateClientImportResponse> ValidateAsync(params ImportClientRow[] rows)
        => await Validator().ValidateAsync(rows.ToList(), _tenantId, CancellationToken.None);

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Accepts_a_complete_individual_row()
    {
        var response = await ValidateAsync(Individual());

        response.TotalRows.Should().Be(1);
        response.ValidRows.Should().Be(1);
        response.Rows[0].IsValid.Should().BeTrue();
        response.Rows[0].ClientType.Should().Be(nameof(ClientType.Individual));
        response.Rows[0].DisplayName.Should().Be("Awa Ouattara");
    }

    [Fact]
    public async Task A_row_with_a_legal_name_is_read_as_a_company()
    {
        await using (var db = _factory.CreateContext())
        {
            db.LegalForms.Add(LegalForm.Create(_tenantId, "SARL", "Société à responsabilité limitée", 10));
            await db.SaveChangesAsync();
        }

        var response = await ValidateAsync(new ImportClientRow
        {
            LegalName = "Sankore Négoce",
            LegalFormCode = "SARL",
            RegistrationNumber = "CI-ABJ-2020-B-1234",
            IncorporationDate = "2020-06-15",
            PhoneNumber = "+2252722000000",
        });

        response.Rows[0].IsValid.Should().BeTrue();
        response.Rows[0].ClientType.Should().Be(nameof(ClientType.Legal));
    }

    // ── what it catches ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reports_every_missing_mandatory_field_at_once()
    {
        // One error per row would make an operator re-run the dry run five times.
        var response = await ValidateAsync(new ImportClientRow { PhoneNumber = null });

        var errors = response.Rows[0].Errors;
        errors.Should().Contain(e => e.Contains("FirstName"));
        errors.Should().Contain(e => e.Contains("LastName"));
        errors.Should().Contain(e => e.Contains("DateOfBirth"));
        errors.Should().Contain(e => e.Contains("Nationality"));
        errors.Should().Contain(e => e.Contains("IdentityDocumentNumber"));
        errors.Should().Contain(e => e.Contains("PhoneNumber"));
    }

    [Fact]
    public async Task Rejects_an_applicant_under_the_minimum_age()
    {
        var tooYoung = DateTime.UtcNow.AddYears(-10).ToString("yyyy-MM-dd");

        var response = await ValidateAsync(Individual(dateOfBirth: tooYoung));

        response.Rows[0].IsValid.Should().BeFalse();
        response.Rows[0].Errors.Should().Contain(e => e.Contains("minimum age"));
    }

    [Fact]
    public async Task Reads_a_day_first_date_the_way_an_operator_wrote_it()
    {
        // 02/04/1987 is 2 April in Abidjan; the invariant parser would read 4 February.
        ClientImportValidator.TryParseDate("02/04/1987", out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(1987, 4, 2));
    }

    [Fact]
    public async Task Rejects_a_date_that_is_not_a_date()
    {
        var response = await ValidateAsync(Individual(dateOfBirth: "not-a-date"));

        response.Rows[0].Errors.Should().Contain(e => e.Contains("not a date"));
    }

    // ── duplicates, without decrypting anything ─────────────────────────────

    [Fact]
    public async Task Flags_a_document_that_already_belongs_to_a_client()
    {
        await using (var db = _factory.CreateContext())
        {
            await TestClientFactory.SeedIndividualAsync(
                db, _tenantId, _agencyId, identityDocumentNumber: "CI0001");
        }

        var response = await ValidateAsync(Individual(document: "CI0001"));

        response.Rows[0].IsValid.Should().BeFalse();
        response.Rows[0].Errors.Should().Contain(e => e.Contains("already has this identity document"));
    }

    [Fact]
    public async Task Flags_two_rows_of_the_file_sharing_a_document()
    {
        // The database cannot see this one: both rows are new.
        var response = await ValidateAsync(
            Individual(document: "CI0001", first: "Awa"),
            Individual(document: "CI0001", first: "Koffi"));

        response.Rows[0].IsValid.Should().BeTrue("the first occurrence is the legitimate one");
        response.Rows[1].Errors.Should().Contain(e => e.Contains("Duplicate identity document within the file"));
    }

    [Fact]
    public async Task Flags_a_registration_number_already_taken()
    {
        await using (var db = _factory.CreateContext())
        {
            db.LegalForms.Add(LegalForm.Create(_tenantId, "SARL", "SARL", 10));
            await db.SaveChangesAsync();
        }

        var row = new ImportClientRow
        {
            LegalName = "Sankore Négoce",
            LegalFormCode = "SARL",
            RegistrationNumber = "CI-ABJ-2020-B-1234",
            IncorporationDate = "2020-06-15",
            PhoneNumber = "+2252722000000",
        };

        var response = await ValidateAsync(row, row);

        response.Rows[1].Errors.Should().Contain(e => e.Contains("Duplicate registration number"));
    }

    [Fact]
    public async Task Rejects_a_legal_form_the_tenant_does_not_use()
    {
        var response = await ValidateAsync(new ImportClientRow
        {
            LegalName = "Sankore Négoce",
            LegalFormCode = "GmbH",
            RegistrationNumber = "X-1",
            IncorporationDate = "2020-06-15",
            PhoneNumber = "+2252722000000",
        });

        response.Rows[0].Errors.Should().Contain(e => e.Contains("not an active legal form"));
    }

    // ── what the report may not contain ─────────────────────────────────────

    [Fact]
    public async Task The_report_never_echoes_an_identity_document_or_a_phone_number()
    {
        // A validation report gets exported, mailed and pasted into tickets.
        var response = await ValidateAsync(Individual(document: "CI-SECRET-42", phone: "+2250708091801"));

        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        serialized.Should().NotContain("CI-SECRET-42");
        serialized.Should().NotContain("0708091801");
    }

    [Fact]
    public async Task Counts_valid_and_invalid_rows()
    {
        var response = await ValidateAsync(
            Individual(document: "CI0001"),
            Individual(document: "CI0002", last: null),
            Individual(document: "CI0003"));

        response.TotalRows.Should().Be(3);
        response.ValidRows.Should().Be(2);
        response.InvalidRows.Should().Be(1);
    }
}
