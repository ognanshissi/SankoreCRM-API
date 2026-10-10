namespace Sankore.Modules.Customers.Tests.Features.Household;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.AddMember;
using Sankore.Modules.Customers.Features.Household.GetHousehold;
using Sankore.Modules.Customers.Features.Household.SetPepStatus;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A client's household on the EXISTING relationship table (BNF-B-13) — no second table, so the
/// household is the filtered view and the removal is already temporal.
/// </summary>
public sealed class ClientHouseholdTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private Guid _clientId;

    public ClientHouseholdTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private AddHouseholdMemberHandler Adder(CustomersDbContext db)
        => new(
            db,
            TestHouseholdContext.Household(db, _tenant, _actor, _publisher),
            TestDoubles.CurrentUser(_tenant, _actor),
            TestDoubles.Encryptor());

    private async Task<Result<Guid>> AddAsync(AddHouseholdMemberCommand cmd)
    {
        await using var db = _factory.CreateContext();
        return await Adder(db).Handle(cmd, CancellationToken.None);
    }

    private static PersonIdentityDto Person(
        string first = "Awa", string last = "Ouattara",
        string? address = null, string? city = null, DateOnly? birthDate = null)
        => new(
            LastName: last, FirstName: first, UsageName: null,
            BirthDate: birthDate, Gender: null,
            AddressLine: address, PostalCode: null, City: city, CountryCode: "CI");

    private AddHouseholdMemberCommand Spouse(
        PersonIdentityDto? identity = null, PepStatusDto? pep = null, bool inherits = false)
        => new(
            ClientId: _clientId,
            Type: nameof(RelationshipType.Spouse),
            Identity: identity ?? Person(),
            SpouseUnionType: nameof(Sankore.Shared.Kernel.ValueObject.SpouseUnionType.Married),
            Pep: pep,
            UsesPrincipalAddress: inherits);

    private AddHouseholdMemberCommand Child(string first = "Kofi", bool inherits = true)
        => new(
            ClientId: _clientId,
            Type: nameof(RelationshipType.Child),
            Identity: Person(first, "Ouattara", birthDate: new DateOnly(2015, 4, 2)),
            SpouseUnionType: null,
            Pep: null,
            UsesPrincipalAddress: inherits);

    private async Task<ClientHouseholdDto> ReadAsync(bool pepDetail = true)
    {
        await using var db = _factory.CreateContext();

        var handler = new GetHouseholdHandler(
            db,
            TestDoubles.CurrentUser(_tenant, _actor),
            TestDoubles.AgencyScope(),
            TestDoubles.Encryptor(),
            TimeProvider.System);

        var result = await handler.Handle(
            new GetHouseholdQuery(_clientId, pepDetail, IncludeRemoved: false),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    // ── the household is the filtered view ──────────────────────────────────

    [Fact]
    public async Task A_spouse_and_a_child_round_trip_as_a_household()
    {
        (await AddAsync(Spouse())).IsSuccess.Should().BeTrue();
        (await AddAsync(Child())).IsSuccess.Should().BeTrue();

        var household = await ReadAsync();

        household.Members.Should().HaveCount(2);
        household.Members.Should().Contain(m =>
            m.Type == nameof(RelationshipType.Spouse) && m.SpouseUnionType == "Married");
        household.Members.Should().Contain(m => m.Identity!.FirstName == "Kofi");
    }

    [Fact]
    public async Task A_guarantor_is_a_relationship_and_not_a_household_member()
    {
        // Admitting one would make the one-spouse invariant and the household read mean something
        // different on each screen.
        var result = await AddAsync(Spouse() with
        {
            Type = nameof(RelationshipType.Guarantor),
            SpouseUnionType = null,
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.NotAHouseholdMember);
    }

    [Fact]
    public async Task At_most_one_active_spouse()
    {
        (await AddAsync(Spouse())).IsSuccess.Should().BeTrue();

        var second = await AddAsync(Spouse(Person("Ama", "Koné")));

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(CustomerErrors.HouseholdAlreadyHasSpouse,
            "the InMemory provider honours no filtered unique index — the handler's check is what "
            + "gives the agent an actionable code, and uq_client_one_spouse covers the race");
    }

    [Fact]
    public async Task A_spouse_needs_a_union_type_and_a_child_may_not_have_one()
    {
        var spouseWithout = await AddAsync(Spouse() with { SpouseUnionType = null });
        var childWith = await AddAsync(Child() with { SpouseUnionType = "Married" });

        spouseWithout.Error.Should().Be(CustomerErrors.UnionTypeRequired);
        childWith.Error.Should().Be(CustomerErrors.UnionTypeForbidden);
    }

    [Fact]
    public async Task Children_count_towards_the_clients_dependents()
    {
        await AddAsync(Child("Kofi"));
        await AddAsync(Child("Ama"));
        await AddAsync(Spouse());

        await using var db = _factory.CreateContext();
        var client = await db.Clients.SingleAsync(CancellationToken.None);

        client.DependentsCount.Should().Be(2, "a spouse is not a dependent");
    }

    // ── address inheritance is a link, not a copy ───────────────────────────

    [Fact]
    public async Task An_inheriting_members_own_postal_fields_are_cleared_on_write()
    {
        // Cleared and not merely ignored on read: a stale address left in the row is one a later
        // "stop inheriting" would silently resurrect, years out of date.
        await AddAsync(Child("Kofi") with
        {
            Identity = Person("Kofi", "Ouattara", address: "12 rue des Jardins", city: "Abidjan"),
            UsesPrincipalAddress = true,
        });

        await using var db = _factory.CreateContext();
        var member = await db.ClientRelationships.SingleAsync(CancellationToken.None);

        member.ExternalAddressEncrypted.Should().BeNull();
        member.ExternalCity.Should().BeNull();
        member.UsesPrincipalAddress.Should().BeTrue();
    }

    [Fact]
    public async Task A_member_with_their_own_address_keeps_it_encrypted()
    {
        await AddAsync(Spouse(
            Person("Awa", "Ouattara", address: "12 rue des Jardins", city: "Abidjan")));

        await using var db = _factory.CreateContext();
        var member = await db.ClientRelationships.SingleAsync(CancellationToken.None);

        member.ExternalAddressEncrypted.Should().NotBeNull();
        member.ExternalAddressEncrypted.Should().NotBe("12 rue des Jardins",
            "the column holds ciphertext");

        // And the read side decrypts it through ResolveIdentity.
        var household = await ReadAsync();
        household.Members.Should().ContainSingle()
                 .Which.Identity!.AddressLine.Should().Be("12 rue des Jardins");
    }

    [Fact]
    public async Task The_display_name_is_kept_in_step_with_the_structured_names()
    {
        // ExternalFullName is what the merge executor, the duplicate scorer and every pre-existing
        // projection read — leaving it stale would show a renamed spouse's old name everywhere
        // except the household screen.
        await AddAsync(Spouse(Person("Awa", "Ouattara")));

        await using var db = _factory.CreateContext();
        var member = await db.ClientRelationships.SingleAsync(CancellationToken.None);

        member.ExternalFullName.Should().Be("Awa Ouattara");
    }

    // ── a member who is a client in their own right ─────────────────────────

    [Fact]
    public async Task A_member_who_is_a_client_keeps_their_identity_on_their_own_record()
    {
        Guid spouseClientId;
        await using (var seed = _factory.CreateContext())
        {
            var other = TestClientFactory.Individual(
                _tenant, _agency, first: "Ama", last: "Koné", clientNumber: "ABJ-2026-000002");

            seed.Clients.Add(other);
            await seed.SaveChangesAsync(CancellationToken.None);
            spouseClientId = other.Id;
        }

        var result = await AddAsync(new AddHouseholdMemberCommand(
            ClientId: _clientId,
            Type: nameof(RelationshipType.Spouse),
            Identity: null,
            SpouseUnionType: "Married",
            Pep: null,
            UsesPrincipalAddress: false,
            RelatedClientId: spouseClientId));

        result.IsSuccess.Should().BeTrue(result.Error);

        var household = await ReadAsync();
        var member = household.Members.Should().ContainSingle().Subject;

        member.RelatedClientId.Should().Be(spouseClientId);
        member.Identity.Should().BeNull("their identity is on the client record, not copied here");
        member.DisplayName.Should().NotBeNullOrWhiteSpace("resolved from the client for the screen");
    }

    [Fact]
    public async Task A_member_pointing_at_a_client_that_does_not_exist_is_refused()
    {
        var result = await AddAsync(new AddHouseholdMemberCommand(
            ClientId: _clientId,
            Type: nameof(RelationshipType.Child),
            Identity: null,
            SpouseUnionType: null,
            Pep: null,
            UsesPrincipalAddress: false,
            RelatedClientId: Guid.NewGuid()));

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ── PEP (BNF-B-13) ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_member_recorded_without_a_PEP_status_has_not_been_asked()
    {
        await AddAsync(Child());

        await using var db = _factory.CreateContext();
        var member = await db.ClientRelationships.SingleAsync(CancellationToken.None);

        member.Pep.IsPep.Should().BeFalse();
        member.PepDeclaredAt.Should().BeNull(
            "stamping it would record an answer nobody gave — see PepDeclaredAt");
    }

    [Fact]
    public async Task Declaring_a_member_exposed_raises_the_clients_flag()
    {
        var memberId = (await AddAsync(Spouse())).Value;

        await using (var db = _factory.CreateContext())
        {
            var handler = new SetHouseholdMemberPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            var result = await handler.Handle(
                new SetHouseholdMemberPepStatusCommand(
                    _clientId, memberId,
                    new PepStatusDto(IsPep: true, Function: "Députée", Link: "Self")),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var read = _factory.CreateContext();
        var client = await read.Clients.SingleAsync(CancellationToken.None);

        client.HasPepExposure.Should().BeTrue(
            "the recalculation runs on every household write, not only on the client's own status");
    }

    [Fact]
    public async Task A_rise_in_exposure_asks_for_a_KYC_review_exactly_once()
    {
        await AddAsync(Spouse());

        await using (var db = _factory.CreateContext())
        {
            var handler = new SetClientPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            await handler.Handle(
                new SetClientPepStatusCommand(
                    _clientId, new PepStatusDto(IsPep: true, Function: "Ministre", Link: "Self")),
                CancellationToken.None);
        }

        var published = _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<ClientPepExposureRaisedEvent>()
            .ToList();

        published.Should().ContainSingle().Which.ClientId.Should().Be(_clientId);

        // And it carries no classification: the consumer opens a review, it does not read why.
        System.Text.Json.JsonSerializer.Serialize(published[0]).Should().NotContain("Ministre");
    }

    [Fact]
    public async Task A_second_declaration_of_the_same_exposure_asks_again_for_nothing()
    {
        await using (var db = _factory.CreateContext())
        {
            var handler = new SetClientPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            await handler.Handle(new SetClientPepStatusCommand(
                _clientId, new PepStatusDto(true, "Ministre", Link: "Self")), CancellationToken.None);
        }

        _publisher.ClearReceivedCalls();

        await using (var db = _factory.CreateContext())
        {
            var handler = new SetClientPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            await handler.Handle(new SetClientPepStatusCommand(
                _clientId, new PepStatusDto(true, "Ministre", Link: "Self")), CancellationToken.None);
        }

        _publisher.ReceivedCalls().Should().BeEmpty("only a RISE is worth a review");
    }

    [Fact]
    public async Task A_client_exposed_as_Self_propagates_onto_the_household()
    {
        var memberId = (await AddAsync(Spouse())).Value;

        await using (var db = _factory.CreateContext())
        {
            var handler = new SetClientPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            await handler.Handle(new SetClientPepStatusCommand(
                _clientId, new PepStatusDto(true, "Ministre", Link: "Self")), CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var member = await read.ClientRelationships
            .SingleAsync(r => r.Id == memberId, CancellationToken.None);

        member.Pep.IsPep.Should().BeTrue();
        member.Pep.Link.Should().Be(Sankore.Shared.Kernel.ValueObject.PepLink.FamilyMember);
    }

    [Fact]
    public async Task A_member_an_agent_classified_individually_is_not_overwritten()
    {
        // The OverrideReason is a judgement BNF-B-13 grants the agent, and the propagation must
        // not discard it.
        var memberId = (await AddAsync(Spouse(
            pep: new PepStatusDto(
                IsPep: false, OverrideReason: "Checked with the client, no mandate")))).Value;

        await using (var db = _factory.CreateContext())
        {
            // A NOT-exposed status carrying an override reason: PepStatus.NotExposed drops the
            // reason, so re-read what was actually stored before asserting the propagation.
            var stored = await db.ClientRelationships
                .SingleAsync(r => r.Id == memberId, CancellationToken.None);

            stored.Pep.OverrideReason.Should().BeNull(
                "an unexposed status carries no classification to override — the guard only "
                + "protects a DECLARED exposure, which is what the propagation would overwrite");
        }

        await using var write = _factory.CreateContext();
        var exposed = await write.ClientRelationships
            .AsTracking()
            .SingleAsync(r => r.Id == memberId, CancellationToken.None);

        exposed.SetPepStatus(
            Sankore.Shared.Kernel.ValueObject.PepStatus.Exposed(
                "Maire", Sankore.Shared.Kernel.ValueObject.PepLink.Self,
                overrideReason: "Own mandate, not the client's"),
            DateTimeOffset.UtcNow);

        await write.SaveChangesAsync(CancellationToken.None);

        await using (var db = _factory.CreateContext())
        {
            var handler = new SetClientPepStatusHandler(
                TestHouseholdContext.Household(db, _tenant, _actor, _publisher));

            await handler.Handle(new SetClientPepStatusCommand(
                _clientId, new PepStatusDto(true, "Ministre", Link: "Self")), CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var member = await read.ClientRelationships
            .SingleAsync(r => r.Id == memberId, CancellationToken.None);

        member.Pep.Link.Should().Be(Sankore.Shared.Kernel.ValueObject.PepLink.Self,
            "the agent's own classification survived the propagation");
    }

    [Fact]
    public async Task The_PEP_detail_is_withheld_without_the_permission_and_the_flag_is_not()
    {
        await AddAsync(Spouse(pep: new PepStatusDto(true, "Députée", Link: "Self")));

        var masked = await ReadAsync(pepDetail: false);
        var member = masked.Members.Should().ContainSingle().Subject;

        member.Pep.Should().BeNull("the function and the country identify a person by themselves");
        member.IsPepExposed.Should().BeTrue("dispatching and designation screens need the flag");
        masked.PrincipalPep.Should().BeNull();
    }
}
