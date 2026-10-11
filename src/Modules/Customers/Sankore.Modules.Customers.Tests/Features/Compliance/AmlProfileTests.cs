namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.AmlProfile;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The LBC/FT profile of a client (CIMA article 13).
///
/// <para>
/// The requirement that drove it is explicit in the text and was not met: « les mentions vagues
/// (commerçant, homme d'affaires) ne suffisent pas ; il faut se renseigner sur le secteur
/// d'activité du client ». <c>Client.Profession</c> is what the client calls themselves; these
/// fields are what the institution established, and before this there was nowhere to put them.
/// </para>
/// </summary>
public sealed class AmlProfileTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public AmlProfileTests() => _factory = new TestCustomersDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenant);
        user.Id.Returns(_actor);
        return user;
    }

    private IAgencyScopeProvider Unrestricted()
    {
        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return scope;
    }

    private async Task<Client> SeedClientAsync()
    {
        await using var db = _factory.CreateContext();
        return await TestClientFactory.SeedAsync(
            db, TestClientFactory.Individual(_tenant, _agencyId));
    }

    private async Task<Sankore.Shared.Kernel.Result> UpdateAsync(
        Guid clientId,
        uint version,
        string? sectorCode = null,
        string? sectorDetail = null,
        string? fundsOriginCode = null,
        string? fundsOriginDetail = null,
        string? corporatePurpose = null,
        DateOnly? extractIssuedOn = null,
        IAgencyScopeProvider? scope = null)
    {
        await using var db = _factory.CreateContext();

        return await new UpdateAmlProfileHandler(
                db, User(), scope ?? Unrestricted(), TestDoubles.Encryptor())
            .Handle(
                new UpdateAmlProfileCommand(
                    clientId, version, sectorCode, sectorDetail, fundsOriginCode,
                    fundsOriginDetail, corporatePurpose, extractIssuedOn),
                CancellationToken.None);
    }

    private async Task<Client> ReloadAsync(Guid clientId)
    {
        await using var db = _factory.CreateContext();
        return await db.Clients.FirstAsync(c => c.Id == clientId);
    }

    [Fact]
    public async Task The_sector_of_activity_is_recorded_as_a_code_and_as_a_narrative()
    {
        var client = await SeedClientAsync();

        var result = await UpdateAsync(
            client.Id, client.Version,
            sectorCode: "retail-food",
            sectorDetail: "Vend du riz et de l'huile sur le marché de Treichville, 3 employés");

        result.IsSuccess.Should().BeTrue(result.Error);

        var stored = await ReloadAsync(client.Id);
        stored.ActivitySectorCode.Should().Be("RETAIL-FOOD", "the code is normalised upper-case");
        stored.ActivitySectorDetail.Should().Contain("Treichville");
    }

    [Fact]
    public async Task The_funds_origin_narrative_is_encrypted_and_never_stored_in_clear()
    {
        // It describes a named person's assets — the most sensitive sentence in a client file
        // after their health. No blind index either: nothing searches for a client by it.
        var client = await SeedClientAsync();
        const string narrative = "Héritage paternel, succession ouverte en 2019";

        await UpdateAsync(
            client.Id, client.Version,
            fundsOriginCode: FundsOriginCodes.Inheritance,
            fundsOriginDetail: narrative);

        var stored = await ReloadAsync(client.Id);

        stored.FundsOriginCode.Should().Be(FundsOriginCodes.Inheritance);

        // What is pinned is that the handler PASSED THE VALUE THROUGH THE ENCRYPTOR rather than
        // writing it straight to the column. This module's test encryptor is a passthrough that
        // prefixes "enc:", so the prefix is the observable proof of that call — the AES-GCM
        // primitive itself is covered in Shared.Infrastructure.Tests, and asserting the
        // ciphertext here would only be asserting against the double.
        stored.EncryptedFundsOriginDetail.Should().NotBe(narrative);
        stored.EncryptedFundsOriginDetail.Should().Be($"enc:{narrative}");
    }

    [Fact]
    public async Task A_blank_narrative_clears_the_column_rather_than_encrypting_emptiness()
    {
        // "Nothing recorded" has to read as null. An empty string encrypted is still a payload,
        // and a compliance screen would show a field that decrypts to nothing as filled in.
        var client = await SeedClientAsync();

        await UpdateAsync(client.Id, client.Version, fundsOriginDetail: "Salaire");

        var withValue = await ReloadAsync(client.Id);
        withValue.EncryptedFundsOriginDetail.Should().NotBeNull();

        await UpdateAsync(client.Id, withValue.Version, fundsOriginDetail: "");

        (await ReloadAsync(client.Id)).EncryptedFundsOriginDetail.Should().BeNull();
    }

    [Fact]
    public async Task A_null_field_leaves_the_previous_value_alone()
    {
        // A compliance file is completed over several conversations from a screen that posts only
        // what it collected. A null that cleared would make each save erase the last.
        var client = await SeedClientAsync();

        await UpdateAsync(client.Id, client.Version, sectorCode: "AGRI");
        var afterFirst = await ReloadAsync(client.Id);

        await UpdateAsync(
            afterFirst.Id, afterFirst.Version, fundsOriginCode: FundsOriginCodes.Agriculture);

        var stored = await ReloadAsync(client.Id);
        stored.ActivitySectorCode.Should().Be("AGRI");
        stored.FundsOriginCode.Should().Be(FundsOriginCodes.Agriculture);
    }

    [Fact]
    public async Task An_unknown_funds_origin_code_is_refused_by_the_validator()
    {
        // The closed list is the whole reason the code exists next to the narrative: a regulator
        // asks how many exposed clients declared an inheritance, and a column of free text
        // answers nothing.
        var validator = new UpdateAmlProfileValidator();

        var bad = validator.Validate(new UpdateAmlProfileCommand(
            Guid.NewGuid(), 1, null, null, "LOTTERY_WIN", null, null, null));

        bad.IsValid.Should().BeFalse();

        var good = validator.Validate(new UpdateAmlProfileCommand(
            Guid.NewGuid(), 1, null, null, FundsOriginCodes.Remittances, null, null, null));

        good.IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_blank_funds_origin_code_is_valid_because_the_field_is_optional()
    {
        // "Not recorded yet" is not an invalid code. What makes it mandatory is the vigilance
        // level, which the KYC approval path knows and this write does not.
        FundsOriginCodes.IsValid(null).Should().BeTrue();
        FundsOriginCodes.IsValid("  ").Should().BeTrue();
        FundsOriginCodes.IsValid("employment").Should().BeTrue("the check is case-insensitive");
    }

    [Fact]
    public async Task A_stale_version_is_a_concurrency_conflict_and_changes_nothing()
    {
        var client = await SeedClientAsync();

        var result = await UpdateAsync(client.Id, version: client.Version + 99, sectorCode: "X");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
        (await ReloadAsync(client.Id)).ActivitySectorCode.Should().BeNull();
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_reads_as_NOT_FOUND()
    {
        // Never forbidden: a 403 would confirm this client exists in another branch.
        var client = await SeedClientAsync();

        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await UpdateAsync(client.Id, client.Version, sectorCode: "X", scope: scope);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ── the registry extract (article 13.2) ─────────────────────────────────

    [Fact]
    public async Task The_registry_extract_date_is_recorded_and_its_freshness_is_one_definition()
    {
        var client = await SeedClientAsync();
        var issued = new DateOnly(2026, 9, 1);

        await UpdateAsync(
            client.Id, client.Version,
            corporatePurpose: "Commerce général, import-export",
            extractIssuedOn: issued);

        var stored = await ReloadAsync(client.Id);
        stored.CorporatePurpose.Should().Contain("import-export");
        stored.RegistrationExtractIssuedOn.Should().Be(issued);

        // Fresh at two months, stale at four — the three-month rule, with the window passed in
        // rather than fixed, so the creation path and the KYC path share one notion of "recent".
        stored.HasFreshRegistrationExtract(new DateOnly(2026, 11, 1), maxAgeMonths: 3)
            .Should().BeTrue();
        stored.HasFreshRegistrationExtract(new DateOnly(2027, 1, 1), maxAgeMonths: 3)
            .Should().BeFalse();
    }

    [Fact]
    public async Task No_extract_on_file_does_not_satisfy_a_requirement_for_a_recent_one()
    {
        var client = await SeedClientAsync();

        (await ReloadAsync(client.Id))
            .HasFreshRegistrationExtract(new DateOnly(2026, 10, 11), maxAgeMonths: 3)
            .Should().BeFalse();
    }

    [Fact]
    public void An_extract_issued_in_the_future_is_refused()
    {
        var validator = new UpdateAmlProfileValidator();

        var result = validator.Validate(new UpdateAmlProfileCommand(
            Guid.NewGuid(), 1, null, null, null, null, null,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)));

        result.IsValid.Should().BeFalse();
    }
}
