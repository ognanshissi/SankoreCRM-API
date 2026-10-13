namespace Sankore.Modules.Customers.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// Does the document agree with the record (KRM-B-08, §3.2 — « comparaison normalisée, sans
/// accents ni casse »)?
///
/// <para>
/// This comparison is one of the conditions that can let a relationship be entered into with no
/// human, so both directions of being wrong are expensive: too strict and every remote file routes
/// to an agent, which makes the feature pointless; too loose and a machine accepts somebody else's
/// identity card for a client record. The cases below are the boundary.
/// </para>
/// </summary>
public sealed class ReadIdentityComparerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Agency = Guid.NewGuid();
    private static readonly DateOnly BirthDate = new(1987, 4, 23);

    private readonly TestCustomersDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Client> SeedAsync(string first = "Awa", string last = "Ouattara")
    {
        await using var db = _factory.CreateContext();

        var client = TestClientFactory.Individual(
            Tenant, Agency, first: first, last: last, dateOfBirth: BirthDate);

        db.Clients.Add(client);
        await db.SaveChangesAsync(CancellationToken.None);
        return client;
    }

    private async Task<bool> MatchesAsync(
        Guid clientId,
        string? surname = "OUATTARA",
        string? given = "AWA",
        DateOnly? birth = null,
        Guid? tenantId = null)
    {
        await using var db = _factory.CreateContext();

        return await TestDoubles.ReadIdentity(db).MatchesAsync(
            tenantId ?? Tenant, clientId, surname, given, birth ?? BirthDate,
            CancellationToken.None);
    }

    [Fact]
    public async Task An_MRZ_reading_of_the_same_person_matches()
    {
        // The ordinary case: a card reads in upper case with no accents, the record holds title
        // case with them. Refusing this would route every single remote file to a human.
        var client = await SeedAsync();

        (await MatchesAsync(client.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Accents_on_the_record_do_not_break_the_comparison()
    {
        var client = await SeedAsync(first: "Aminata", last: "Traoré");

        (await MatchesAsync(client.Id, surname: "TRAORE", given: "AMINATA")).Should().BeTrue();
    }

    [Theory]
    [InlineData("N'GUESSAN", "N GUESSAN")]
    [InlineData("KONE-BAMBA", "KONE BAMBA")]
    public async Task Punctuation_is_collapsed_the_same_way_on_both_sides(
        string recorded, string read)
    {
        // SearchKeyBuilder.NormalizeTerm collapses every run of non-alphanumerics to one space,
        // and the point of reusing it is that the two sides cannot disagree about it. Ivorian
        // surnames carry apostrophes and hyphens that an extractor renders however it likes.
        var client = await SeedAsync(last: recorded);

        (await MatchesAsync(client.Id, surname: read)).Should().BeTrue();
    }

    [Fact]
    public async Task A_card_listing_MORE_given_names_than_the_record_still_matches()
    {
        // A document lists a person's full civil given names; a counter capture holds what they
        // gave. "AWA" on the record against "AWA MARIE" on the card is the same person with one
        // name omitted at capture — and it is common enough that failing it would make the
        // condition stop discriminating.
        var client = await SeedAsync(first: "Awa");

        (await MatchesAsync(client.Id, given: "AWA MARIE")).Should().BeTrue();
    }

    [Fact]
    public async Task A_card_listing_FEWER_given_names_than_the_record_does_NOT()
    {
        // The direction that matters. A name on the record absent from the document means the
        // document is not this person's, and a human looks at it.
        var client = await SeedAsync(first: "Awa Marie");

        (await MatchesAsync(client.Id, given: "AWA")).Should().BeFalse();
    }

    [Fact]
    public async Task Given_names_in_a_different_ORDER_still_match()
    {
        // An MRZ and a counter capture routinely disagree about the order of compound given names.
        var client = await SeedAsync(first: "Awa Marie");

        (await MatchesAsync(client.Id, given: "MARIE AWA")).Should().BeTrue();
    }

    [Fact]
    public async Task A_different_surname_does_not_match()
    {
        var client = await SeedAsync();

        (await MatchesAsync(client.Id, surname: "KONE")).Should().BeFalse();
    }

    [Fact]
    public async Task The_surname_is_strict_and_a_PARTIAL_one_is_refused()
    {
        // Unlike the given names, the surname is one field on both sides and is compared whole.
        // "OUATTARA" against a recorded "OUATTARA KONE" is a different person until a human says
        // otherwise.
        var client = await SeedAsync(last: "Ouattara Kone");

        (await MatchesAsync(client.Id, surname: "OUATTARA")).Should().BeFalse();
    }

    [Fact]
    public async Task A_different_date_of_birth_does_not_match()
    {
        var client = await SeedAsync();

        (await MatchesAsync(client.Id, birth: new DateOnly(1987, 4, 24))).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "AWA")]
    [InlineData("OUATTARA", null)]
    [InlineData("", "AWA")]
    [InlineData("OUATTARA", "   ")]
    public async Task An_INCOMPLETE_reading_is_not_a_match(string? surname, string? given)
    {
        // The fail-closed case, and the one most likely to be got wrong. A document whose surname
        // the extractor could not produce is not a document that agrees with the record — it is a
        // document nobody checked. §3.2's condition is a positive one, so the only honest answer
        // when there is nothing to compare is no, and the file routes to a human.
        var client = await SeedAsync();

        (await MatchesAsync(client.Id, surname: surname, given: given)).Should().BeFalse();
    }

    [Fact]
    public async Task A_reading_with_no_date_of_birth_is_not_a_match()
    {
        var client = await SeedAsync();

        // NOT through MatchesAsync: its `birth ?? BirthDate` would substitute the default and the
        // null would never reach the comparer — the assertion would pass on the wrong input.
        await using var db = _factory.CreateContext();
        (await TestDoubles.ReadIdentity(db).MatchesAsync(
                Tenant, client.Id, "OUATTARA", "AWA", null, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_client_is_not_a_match()
    {
        (await MatchesAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task A_client_of_ANOTHER_tenant_is_not_a_match()
    {
        // The caller is a background consumer reading with IgnoreQueryFilters, so the tenant
        // predicate inside the comparer is the only isolation there is.
        var client = await SeedAsync();

        (await MatchesAsync(client.Id, tenantId: Guid.NewGuid())).Should().BeFalse();
    }
}
