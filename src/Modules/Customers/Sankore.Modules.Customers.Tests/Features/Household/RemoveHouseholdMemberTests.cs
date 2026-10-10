namespace Sankore.Modules.Customers.Tests.Features.Household;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.AddMember;
using Sankore.Modules.Customers.Features.Household.RemoveMember;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Removing a household member (BNF-B-13): logical, with a reason, and the designations the person
/// held move to <c>ToReview</c> — never deleted, never silently reallocated.
/// </summary>
public sealed class RemoveHouseholdMemberTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly Guid _clientId;

    public RemoveHouseholdMemberTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> AddSpouseAsync()
    {
        await using var db = _factory.CreateContext();

        var handler = new AddHouseholdMemberHandler(
            db,
            TestHouseholdContext.Household(db, _tenant, _actor, _publisher),
            TestDoubles.CurrentUser(_tenant, _actor),
            TestDoubles.Encryptor());

        var result = await handler.Handle(new AddHouseholdMemberCommand(
            ClientId: _clientId,
            Type: nameof(RelationshipType.Spouse),
            Identity: new PersonIdentityDto("Ouattara", "Awa"),
            SpouseUnionType: "Married",
            Pep: null,
            UsesPrincipalAddress: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    /// <summary>A live holding with the member on 60% of a death-benefit clause.</summary>
    private async Task<Guid> SeedHoldingDesignatingAsync(Guid memberId)
    {
        var line = TestHoldingFactory.Line();

        await using var db = _factory.CreateContext();
        var holding = TestHoldingFactory.Draft(_tenant, _clientId, line, _actor, DateTimeOffset.UtcNow);

        holding.Designate(
            [
                TestHoldingFactory.Beneficiary(
                    60m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: memberId),
                TestHoldingFactory.Beneficiary(40m, name: "Kofi Ouattara"),
            ],
            line.AllowedRoles, _actor, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync(CancellationToken.None);
        return holding.Id;
    }

    private async Task<Result<RemoveHouseholdMemberResult>> RemoveAsync(
        Guid memberId, string reason = "Séparation déclarée par le client")
    {
        await using var db = _factory.CreateContext();

        var handler = new RemoveHouseholdMemberHandler(
            db,
            TestHouseholdContext.Household(db, _tenant, _actor, _publisher),
            TestDoubles.CurrentUser(_tenant, _actor),
            _publisher);

        return await handler.Handle(
            new RemoveHouseholdMemberCommand(_clientId, memberId, reason), CancellationToken.None);
    }

    // ── the removal itself ──────────────────────────────────────────────────

    [Fact]
    public async Task The_row_is_closed_and_kept()
    {
        var memberId = await AddSpouseAsync();

        (await RemoveAsync(memberId)).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var member = await db.ClientRelationships
            .SingleAsync(r => r.Id == memberId, CancellationToken.None);

        member.IsActive.Should().BeFalse();
        member.ValidTo.Should().NotBeNull();
        member.CloseReason.Should().Be("Séparation déclarée par le client");
        member.ExternalFullName.Should().Be("Awa Ouattara",
            "a household that forgets a separation cannot answer who was covered at a claim");
    }

    [Fact]
    public async Task A_removal_without_a_reason_is_refused()
    {
        var memberId = await AddSpouseAsync();

        (await RemoveAsync(memberId, "   ")).Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Removing_the_spouse_frees_the_slot_for_a_new_one()
    {
        var memberId = await AddSpouseAsync();
        await RemoveAsync(memberId);

        // The one-spouse invariant is over ACTIVE rows, which is the whole point of ValidTo.
        var second = await AddSpouseAsync();
        second.Should().NotBe(memberId);
    }

    [Fact]
    public async Task A_member_of_another_client_is_not_found()
    {
        (await RemoveAsync(Guid.NewGuid())).Error
            .Should().Be(CustomerErrors.RelationshipNotFound);
    }

    // ── the designations the member held (BNF-B-13) ─────────────────────────

    [Fact]
    public async Task Their_designations_move_to_review_and_the_share_is_not_reallocated()
    {
        var memberId = await AddSpouseAsync();
        var holdingId = await SeedHoldingDesignatingAsync(memberId);

        var result = await RemoveAsync(memberId);

        result.Value.DesignationsToReview.Should().Be(1);
        result.Value.AffectedHoldingIds.Should().Equal([holdingId]);

        await using var db = _factory.CreateContext();
        var designations = await db.BeneficiaryDesignations
            .Where(d => d.HoldingId == holdingId)
            .ToListAsync(CancellationToken.None);

        var affected = designations.Single(d => d.SubjectId == memberId);
        affected.Status.Should().Be(DesignationStatus.ToReview);
        affected.StatusReason.Should().Be("HOUSEHOLD_MEMBER_REMOVED");
        affected.SharePercent.Should().Be(60m,
            "the rank no longer totals 100 — and that is exactly what the agent has to be shown");

        designations.Single(d => d.SubjectId == null)
                    .Status.Should().Be(DesignationStatus.Active, "nobody else is touched");
    }

    [Fact]
    public async Task The_agent_is_alerted_when_designations_were_affected()
    {
        var memberId = await AddSpouseAsync();
        var holdingId = await SeedHoldingDesignatingAsync(memberId);
        _publisher.ClearReceivedCalls();

        await RemoveAsync(memberId);

        var alert = _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<HouseholdMemberRemovedEvent>()
            .Should().ContainSingle().Subject;

        alert.ClientId.Should().Be(_clientId);
        alert.MemberId.Should().Be(memberId);
        alert.DesignationsToReview.Should().Be(1);
        alert.AffectedHoldingIds.Should().Equal([holdingId]);
        alert.RemovedBy.Should().Be(_actor);

        // Counts and ids, never a name: the designations were not deleted and the agent reads them
        // on the holdings this points to.
        System.Text.Json.JsonSerializer.Serialize(alert).Should().NotContain("Awa");
    }

    [Fact]
    public async Task A_removal_that_affects_no_designation_notifies_nobody()
    {
        // Ordinary housekeeping. Notifying on it would train the recipient to ignore the ones that
        // matter.
        var memberId = await AddSpouseAsync();
        _publisher.ClearReceivedCalls();

        var result = await RemoveAsync(memberId);

        result.Value.DesignationsToReview.Should().Be(0);
        _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<HouseholdMemberRemovedEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_closed_designation_of_a_superseded_clause_is_left_alone()
    {
        // Only the clause AS IT STANDS is flagged: a superseded version records who was designated
        // at the time and must not be rewritten by a later household change.
        var memberId = await AddSpouseAsync();
        var line = TestHoldingFactory.Line();
        Guid holdingId;

        await using (var seed = _factory.CreateContext())
        {
            var holding = TestHoldingFactory.Draft(_tenant, _clientId, line, _actor, DateTimeOffset.UtcNow);

            holding.Designate(
                [
                    TestHoldingFactory.Beneficiary(
                        100m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: memberId),
                ], line.AllowedRoles, _actor, DateTimeOffset.UtcNow);

            // Replace the clause in draft, which closes the first designation.
            holding.Designate(
                [TestHoldingFactory.Beneficiary(100m, name: "Kofi")],
                line.AllowedRoles, _actor, DateTimeOffset.UtcNow);

            seed.ProductHoldings.Add(holding);
            await seed.SaveChangesAsync(CancellationToken.None);
            holdingId = holding.Id;
        }

        var result = await RemoveAsync(memberId);

        result.Value.DesignationsToReview.Should().Be(0);

        await using var db = _factory.CreateContext();
        var closed = await db.BeneficiaryDesignations
            .SingleAsync(d => d.HoldingId == holdingId && d.SubjectId == memberId,
                         CancellationToken.None);

        closed.Status.Should().Be(DesignationStatus.Closed, "not re-flagged");
    }
}
