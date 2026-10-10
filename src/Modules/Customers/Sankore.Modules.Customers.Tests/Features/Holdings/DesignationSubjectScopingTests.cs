namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Designate;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// A designated subject must be somebody the holding may actually designate (BNF-B-15), and the
/// filterless sync read must be scoped to the tenant AND the client.
///
/// <para>
/// Both halves were missing, and together they formed a leak: any non-empty Guid was accepted as a
/// <c>HouseholdMember</c> subject, and <c>HoldingSyncService</c> then read that relationship's date
/// of birth with the query filters OFF and no tenant predicate — carrying another tenant's
/// identity into the payload sent to an insurer. Unguessable ids were the only thing standing in
/// the way, and an unguessable id is not an access control.
/// </para>
/// </summary>
public sealed class DesignationSubjectScopingTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _lineId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly Guid _clientId;

    public DesignationSubjectScopingTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
              .Returns(TestHoldingFactory.Line(productLineId: _lineId));
        _admin.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
              .Returns(TestHouseholdContext.Compliance);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>A household member of <paramref name="ofClientId"/> in <paramref name="inTenant"/>.</summary>
    private async Task<Guid> SeedMemberAsync(
        Guid? inTenant = null, Guid? ofClientId = null,
        string first = "Awa", DateOnly? birthDate = null, bool removed = false)
    {
        await using var db = _factory.CreateContext();

        var member = ClientRelationship.HouseholdMember(
            tenantId:             inTenant ?? _tenant,
            clientId:             ofClientId ?? _clientId,
            type:                 RelationshipType.Child,
            identity:             PersonIdentity.Create(
                                      "Ouattara", first,
                                      birthDate: birthDate ?? new DateOnly(2015, 4, 2)),
            spouseUnionType:      null,
            pep:                  null,
            usesPrincipalAddress: false,
            encryptAddress:       plain => plain,
            validFrom:            DateTimeOffset.UtcNow,
            createdBy:            _actor);

        if (removed)
            member.Close("Séparation", DateTimeOffset.UtcNow);

        db.ClientRelationships.Add(member);
        await db.SaveChangesAsync(CancellationToken.None);
        return member.Id;
    }

    private async Task<Guid> SeedHoldingAsync()
    {
        await using var db = _factory.CreateContext();

        var holding = TestHoldingFactory.Draft(
            _tenant, _clientId, TestHoldingFactory.Line(productLineId: _lineId),
            _actor, DateTimeOffset.UtcNow);

        db.ProductHoldings.Add(holding);
        await db.SaveChangesAsync(CancellationToken.None);
        return holding.Id;
    }

    private async Task<Result<DesignateResult>> DesignateAsync(
        Guid holdingId, DesignationSubjectType type, Guid subjectId)
    {
        await using var db = _factory.CreateContext();

        var handler = new DesignateHandler(
            TestHouseholdContext.Holdings(db, _tenant, _actor, _admin),
            new DesignationSubjectValidator(db),
            _publisher);

        return await handler.Handle(
            new DesignateCommand(holdingId,
            [
                new DesignationInput(
                    RoleCode: "BENEFICIAIREDECES",
                    SubjectType: type.ToString(),
                    SubjectId: subjectId,
                    Rank: 1,
                    SharePercent: 100m),
            ]),
            CancellationToken.None);
    }

    // ── the subject must belong to this household ───────────────────────────

    [Fact]
    public async Task A_member_of_this_clients_household_is_accepted()
    {
        var holdingId = await SeedHoldingAsync();
        var memberId = await SeedMemberAsync();

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.HouseholdMember, memberId);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_Guid_that_is_nobody_is_refused()
    {
        // It used to be stored, and reached an insurer as a beneficiary with no name.
        var holdingId = await SeedHoldingAsync();

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.HouseholdMember, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationSubjectNotInHousehold);
    }

    [Fact]
    public async Task A_member_of_ANOTHER_CLIENT_of_the_same_tenant_is_refused()
    {
        var holdingId = await SeedHoldingAsync();

        Guid otherClientId;
        await using (var db = _factory.CreateContext())
        {
            var other = TestClientFactory.Individual(
                _tenant, _agency, first: "Ama", last: "Koné", clientNumber: "ABJ-2026-000002");

            db.Clients.Add(other);
            await db.SaveChangesAsync(CancellationToken.None);
            otherClientId = other.Id;
        }

        var foreignMember = await SeedMemberAsync(ofClientId: otherClientId, first: "Kofi");

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.HouseholdMember, foreignMember);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectNotInHousehold);
    }

    [Fact]
    public async Task A_member_of_ANOTHER_TENANT_is_refused()
    {
        var holdingId = await SeedHoldingAsync();
        var foreignMember = await SeedMemberAsync(inTenant: _otherTenant, first: "Fatou");

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.HouseholdMember, foreignMember);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectNotInHousehold);
    }

    [Fact]
    public async Task A_member_who_has_already_left_the_household_is_refused()
    {
        // ToReview exists for somebody who left AFTER being designated. Designating somebody who
        // has already gone is a different event, and an error rather than a flag.
        var holdingId = await SeedHoldingAsync();
        var gone = await SeedMemberAsync(removed: true);

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.HouseholdMember, gone);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectNotInHousehold);
    }

    [Fact]
    public async Task A_client_of_ANOTHER_TENANT_is_refused()
    {
        var holdingId = await SeedHoldingAsync();

        Guid foreignClientId;
        await using (var db = _factory.CreateContext())
        {
            var foreign = TestClientFactory.Individual(
                _otherTenant, Guid.NewGuid(), first: "Moussa", last: "Diallo",
                clientNumber: "BKO-2026-000001");

            db.Clients.Add(foreign);
            await db.SaveChangesAsync(CancellationToken.None);
            foreignClientId = foreign.Id;
        }

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.Client, foreignClientId);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectNotFound);
    }

    [Fact]
    public async Task A_client_of_THIS_tenant_is_accepted()
    {
        // A client legitimately designates a spouse who banks here too, and BNF-B-15 allows a
        // co-holder — so this must keep working.
        var holdingId = await SeedHoldingAsync();

        var result = await DesignateAsync(
            holdingId, DesignationSubjectType.Client, _clientId);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task An_external_person_needs_no_lookup()
    {
        // The common case for a death benefit, and it must not pay for two queries.
        var holdingId = await SeedHoldingAsync();

        await using var db = _factory.CreateContext();

        var handler = new DesignateHandler(
            TestHouseholdContext.Holdings(db, _tenant, _actor, _admin),
            new DesignationSubjectValidator(db),
            _publisher);

        var result = await handler.Handle(
            new DesignateCommand(holdingId,
            [
                new DesignationInput(
                    RoleCode: "BENEFICIAIREDECES",
                    SubjectType: nameof(DesignationSubjectType.ExternalPerson),
                    SubjectId: null,
                    ExternalFullName: "Un ami",
                    Rank: 1,
                    SharePercent: 100m),
            ]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    // ── the filterless sync read is scoped too ──────────────────────────────

    [Fact]
    public async Task The_sync_read_never_returns_another_tenants_date_of_birth()
    {
        // Defence in depth: the validator above stops such a row being WRITTEN, and this stops a
        // row that already exists — one stored before the validator shipped — from being acted on.
        // The designation is forced in through the aggregate, bypassing the handler, because the
        // handler is exactly what now refuses it.
        var foreignMember = await SeedMemberAsync(
            inTenant: _otherTenant, first: "Fatou", birthDate: new DateOnly(1988, 7, 3));

        Guid holdingId;
        await using (var db = _factory.CreateContext())
        {
            var line = TestHoldingFactory.Line(productLineId: _lineId);
            var holding = TestHoldingFactory.Draft(
                _tenant, _clientId, line, _actor, DateTimeOffset.UtcNow);

            holding.Designate(
                [
                    TestHoldingFactory.Beneficiary(
                        100m,
                        subjectType: DesignationSubjectType.HouseholdMember,
                        subjectId: foreignMember),
                ],
                line.AllowedRoles, _actor, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();

            db.ProductHoldings.Add(holding);
            await db.SaveChangesAsync(CancellationToken.None);
            holdingId = holding.Id;
        }

        await using var read = _factory.CreateContext();
        var sync = TestHoldingSync.For(read, _tenant, _admin);

        var forSync = await sync.GetForSyncAsync(_tenant, holdingId, CancellationToken.None);

        forSync.Should().NotBeNull();
        var designation = forSync!.Designations.Should().ContainSingle().Subject;

        designation.BirthDate.Should().BeNull(
            "the query is filterless by necessity, so it must carry its own tenant and client "
            + "predicates — an unguessable id is not an access control");

        // And nothing of that person's identity travels either: the label resolver runs under the
        // global tenant filter, so it finds nothing for a foreign relationship.
        designation.FullName.Should().NotContain("Fatou");
    }

    [Fact]
    public async Task The_sync_read_DOES_return_a_members_date_of_birth()
    {
        // The contrast, so the test above is not passing because the feature is broken: an
        // insurer needs the dates it is entitled to.
        var memberId = await SeedMemberAsync(birthDate: new DateOnly(2015, 4, 2));
        var holdingId = await SeedHoldingAsync();

        (await DesignateAsync(holdingId, DesignationSubjectType.HouseholdMember, memberId))
            .IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var sync = TestHoldingSync.For(read, _tenant, _admin);

        var forSync = await sync.GetForSyncAsync(_tenant, holdingId, CancellationToken.None);

        forSync!.Designations.Should().ContainSingle()
            .Which.BirthDate.Should().Be(new DateOnly(2015, 4, 2));
    }
}
