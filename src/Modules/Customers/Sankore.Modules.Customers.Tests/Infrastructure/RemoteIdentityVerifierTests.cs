namespace Sankore.Modules.Customers.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// The two questions a customer answers on the public remote-KYC page (KRM-B-05).
///
/// <para>
/// What is actually being pinned here is that the ANSWER is a boolean and that no combination of
/// inputs gets more out of it. The endpoint in front of this is anonymous and rate-limited by a
/// per-link counter, so the only thing standing between a leaked link and somebody else's identity
/// documents is that this comparison cannot be probed one field at a time.
/// </para>
/// </summary>
public sealed class RemoteIdentityVerifierTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Agency = Guid.NewGuid();
    private static readonly DateOnly BirthDate = new(1987, 4, 23);
    private const string ClientNumber = "ABJ-PLT-2026-000042";

    private readonly TestCustomersDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Client> SeedAsync(DateOnly? birthDate = null, string? number = null)
    {
        await using var db = _factory.CreateContext();

        var client = TestClientFactory.Individual(
            Tenant, Agency,
            clientNumber: number ?? ClientNumber,
            dateOfBirth: birthDate ?? BirthDate);

        db.Clients.Add(client);
        await db.SaveChangesAsync(CancellationToken.None);
        return client;
    }

    private async Task<bool> VerifyAsync(
        Guid clientId, string? number, DateOnly? birthDate, Guid? tenantId = null)
    {
        await using var db = _factory.CreateContext();
        return await TestDoubles.RemoteIdentity(db)
            .VerifyAsync(tenantId ?? Tenant, clientId, number, birthDate, CancellationToken.None);
    }

    [Fact]
    public async Task The_right_number_and_the_right_date_pass()
    {
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, ClientNumber, BirthDate)).Should().BeTrue();
    }

    [Theory]
    [InlineData("abj-plt-2026-000042")]
    [InlineData("  ABJ-PLT-2026-000042  ")]
    [InlineData("ABJ-PLT-2026-000042 ")]
    [InlineData("ABJ-PLT-2026- 000042")]
    public async Task Casing_and_stray_whitespace_are_forgiven(string typed)
    {
        // A customer copying a number out of an email loses the case and drags a trailing space,
        // and a phone keyboard likes to insert one after a hyphen. Refusing those would burn an
        // identification attempt on a transcription artefact — and there are only five.
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, typed, BirthDate)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ABJPLT2026000042")]
    [InlineData("ABJ PLT 2026 000042")]
    public async Task The_separators_themselves_are_NOT_forgiven(string typed)
    {
        // Dropping the hyphens, or accepting spaces INSTEAD of them, both collapse the tenant's
        // format into one digit string — which is a meaningfully larger set of values that open
        // the same door. The whitespace rule above is about characters a customer adds, not about
        // characters the format requires.
        //
        // The second case looks like a near-miss worth forgiving and is not: it normalises to the
        // same thing as the first, so accepting it would mean accepting both.
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, typed, BirthDate)).Should().BeFalse();
    }

    [Fact]
    public async Task A_wrong_date_fails_even_with_the_right_number()
    {
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, ClientNumber, BirthDate.AddDays(1))).Should().BeFalse();
    }

    [Fact]
    public async Task A_wrong_number_fails_even_with_the_right_date()
    {
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, "ABJ-PLT-2026-000043", BirthDate)).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_date_fails()
    {
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, ClientNumber, null)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_number_fails(string? number)
    {
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, number, BirthDate)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_client_fails()
    {
        (await VerifyAsync(Guid.NewGuid(), ClientNumber, BirthDate)).Should().BeFalse();
    }

    [Fact]
    public async Task A_client_with_no_recorded_date_of_birth_cannot_identify_remotely()
    {
        // The deliberate consequence: a lead converted without a birth date has no second factor,
        // so the client number alone would be the whole credential — and that number is printed in
        // an email. Such a customer is collected at the counter.
        //
        // Built through the aggregate rather than TestClientFactory, which always supplies a date:
        // the case under test is the one a converted lead with no birth date produces, and the
        // factory cannot express it.
        var client = Client.CreateIndividual(
            tenantId: Tenant,
            clientNumber: ClientNumber,
            agencyId: Agency,
            agencyCode: "ABJ",
            advisorUserId: null,
            firstName: "Awa",
            lastName: "Ouattara",
            maidenName: null,
            gender: Gender.Female,
            encryptedDateOfBirth: null,
            dateOfBirthBlindIndex: null,
            birthPlace: null,
            nationality: "CI",
            maritalStatus: MaritalStatus.Single,
            fatherName: null,
            motherName: null,
            profession: null,
            employer: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: "fr",
            docType: null,
            encryptedDocNumber: null,
            docNumberBlindIndex: null,
            docIssuedOn: null,
            docExpiresOn: null,
            createdBy: Guid.NewGuid(),
            sourceLeadId: Guid.NewGuid());

        await using (var db = _factory.CreateContext())
        {
            db.Clients.Add(client);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await VerifyAsync(client.Id, ClientNumber, BirthDate)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_client_fails_even_with_both_values_right()
    {
        // The caller is anonymous and names the client id itself, via the invitation. The tenant
        // predicate is what stops a link of one institution from resolving a client of another.
        var client = await SeedAsync();

        (await VerifyAsync(client.Id, ClientNumber, BirthDate, tenantId: Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public async Task The_contract_returns_a_boolean_and_nothing_else()
    {
        // Pinned as a shape, because the temptation on the next change is to return "which field
        // was wrong" to improve the error message — and that single addition turns an anonymous
        // endpoint into a client-number oracle.
        var method = typeof(Sankore.Modules.Customers.PublicApi.ICustomersModule)
            .GetMethod(nameof(Sankore.Modules.Customers.PublicApi.ICustomersModule
                .VerifyRemoteIdentityAsync));

        method!.ReturnType.Should().Be(typeof(Task<bool>));
    }

    // ── Identifying from the number (WhatsApp complaint intake, 2026-10-26) ──────

    private async Task<Guid?> IdentifyAsync(string number, DateOnly birthDate)
    {
        await using var db = _factory.CreateContext();
        return await TestDoubles.RemoteIdentity(db).IdentifyAsync(Tenant, number, birthDate, CancellationToken.None);
    }

    [Fact]
    public async Task The_number_and_the_date_together_designate_the_client_whatever_the_casing()
    {
        var client = await SeedAsync();

        (await IdentifyAsync(ClientNumber, BirthDate)).Should().Be(client.Id);
        (await IdentifyAsync(" abj-plt-2026-000042 ", BirthDate)).Should().Be(client.Id);
    }

    [Fact]
    public async Task A_wrong_date_or_an_unknown_number_designates_nobody_and_says_nothing_more()
    {
        await SeedAsync();

        (await IdentifyAsync(ClientNumber, BirthDate.AddDays(1))).Should().BeNull();
        (await IdentifyAsync("ABJ-PLT-2026-999999", BirthDate)).Should().BeNull();
        (await IdentifyAsync("", BirthDate)).Should().BeNull();
    }

    [Fact]
    public async Task A_merged_record_is_nobody_to_answer()
    {
        await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Clients.AsTracking().SingleAsync();
            tracked.MarkMerged(Guid.NewGuid(), Guid.NewGuid()).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        (await IdentifyAsync(ClientNumber, BirthDate)).Should().BeNull();
    }
}
