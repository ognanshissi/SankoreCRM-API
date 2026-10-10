namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers recording a health consent (BNF-B-09), and in particular the separation the security
/// review caught: the AGENT who recorded it is not the PERSON who gave it.
/// </summary>
public sealed class RecordHealthConsentHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public RecordHealthConsentHandlerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private RecordHealthConsentHandler Handler(
        Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenant);
        user.Roles.Returns([Roles.CommercialAgent.Code]);

        return new RecordHealthConsentHandler(
            db,
            new LeadAccessGuard(user, _admin),
            new HealthConsentGuard(db),
            user,
            new FrozenClock(Now));
    }

    /// <summary>A lead owned by the acting agent, with a minor child and an adult spouse.</summary>
    private async Task<(Lead Lead, Guid ChildId, Guid SpouseId)> SeedAsync()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, firstName: "Aya", lastName: "Kouassi",
            ownerId: _agent);

        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa", birthDate: new DateOnly(1990, 4, 2)),
            null, true, Now).Value;

        var child = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2018, 6, 1)),
            null, true, Now).Value;

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);

        return (lead, child.Id, spouse.Id);
    }

    private static RecordHealthConsentCommand Command(
        Guid leadId, Guid subjectId, string subjectType = "HouseholdMember", Guid? givenBy = null)
        => new(leadId, subjectType, subjectId, "Paper", "signature-2026-10-10", givenBy);

    private async Task<LeadConsent> StoredAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.LeadConsents.SingleAsync(CancellationToken.None);
    }

    // ── The audit integrity the review flagged ────────────────────────────

    [Fact]
    public async Task The_recorder_is_always_the_authenticated_agent_never_the_caller_supplied_giver()
    {
        // The defect: GivenById used to overwrite RecordedBy, so an agent could erase themselves
        // from — or attribute to a colleague — the one record a regulator asks about first.
        var (lead, childId, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(
            Command(lead.Id, childId, givenBy: lead.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var stored = await StoredAsync();
        stored.RecordedBy.Should().Be(_agent, "the actor is the authenticated user, full stop");
        stored.GivenBy.Should().Be(lead.Id, "and the giver is a field of its own (BNF-B-09)");
    }

    [Fact]
    public async Task An_unidentifiable_giver_is_refused()
    {
        // A consent attributed to an id nobody can resolve is not evidence of anything.
        var (lead, childId, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(
            Command(lead.Id, childId, givenBy: Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOneOf(HealthConsentGuard.RespondentInvalid, "INVALID_CONSENT_GIVER");
    }

    // ── Who may consent for a minor (BNF-B-09, same rule as BNF-B-08) ────

    [Fact]
    public async Task A_minor_needs_a_giver()
    {
        var (lead, childId, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(
            Command(lead.Id, childId, givenBy: null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HealthConsentGuard.RespondentRequired);
    }

    [Fact]
    public async Task The_principal_may_consent_for_a_minor()
    {
        var (lead, childId, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(Command(lead.Id, childId, givenBy: lead.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_spouse_may_consent_for_a_minor()
    {
        var (lead, childId, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(Command(lead.Id, childId, givenBy: spouseId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_adult_consents_for_themselves_with_no_giver()
    {
        var (lead, _, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(
            Command(lead.Id, spouseId, givenBy: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await StoredAsync()).GivenBy.Should().BeNull();
    }

    // ── The rest of the contract ──────────────────────────────────────────

    [Fact]
    public async Task A_subject_who_is_not_of_this_lead_is_refused()
    {
        var (lead, _, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(
                Command(lead.Id, Guid.NewGuid(), givenBy: lead.Id), CancellationToken.None))
            .Error.Should().Be("QUALIFICATION_SUBJECT_NOT_FOUND");
    }

    [Fact]
    public async Task Recording_twice_returns_the_same_consent()
    {
        // A double-submitted form must not produce two rows — both would then have to be withdrawn
        // to stop collection.
        var (lead, _, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var first = await Handler(db).Handle(
            Command(lead.Id, spouseId), CancellationToken.None);
        var second = await Handler(db).Handle(
            Command(lead.Id, spouseId), CancellationToken.None);

        second.Value.Should().Be(first.Value);

        await using var read = _factory.CreateContext();
        (await read.LeadConsents.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task A_lead_the_caller_does_not_hold_is_refused()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, ownerId: Guid.NewGuid());   // somebody else's

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        _admin.GetTeamAgentIdsAsync(_tenant, _agent, Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(
                Command(lead.Id, lead.Id, subjectType: "Principal"), CancellationToken.None))
            .Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    [Fact]
    public async Task The_proof_and_the_channel_are_stored()
    {
        var (lead, _, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(Command(lead.Id, spouseId), CancellationToken.None);

        var stored = await StoredAsync();
        stored.Type.Should().Be(ConsentType.HealthData);
        stored.Status.Should().Be(ConsentStatus.Active);
        stored.Channel.Should().Be(ConsentChannel.Paper);
        stored.ProofReference.Should().Be("signature-2026-10-10");
        stored.SubjectType.Should().Be(QualificationSubjectType.HouseholdMember);
        stored.SubjectId.Should().Be(spouseId);
    }
}
