namespace Sankore.Modules.Leads.Tests.Features.Fatca;

using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Fatca;
using Sankore.Modules.Leads.Features.Fatca.Events;
using Sankore.Modules.Leads.Features.Fatca.SubmitDeclaration;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers recording a FATCA declaration (BNF-B-11): the tenant gate, who is in scope, the
/// supersession, and the event the compliance officer hears.
/// </summary>
public sealed class SubmitFatcaDeclarationHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Signed = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public SubmitFatcaDeclarationHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        FatcaIs(enabled: true, forCoHolders: true);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void FatcaIs(bool enabled, bool forCoHolders = true, string requiredAt = "Conversion") =>
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns(new ComplianceSettingsDto(
                FatcaEnabled: enabled,
                FatcaRegime: enabled ? "IgaModel1" : null,
                FatcaGiin: enabled ? "98Q96B.00000.LE.384" : null,
                FatcaRequiredAt: requiredAt,
                FatcaForCoHolders: forCoHolders,
                PepEnabled: true,
                PepPostMandateMonths: 12));

    private SubmitFatcaDeclarationHandler Handler(
        Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenant);
        user.Roles.Returns([Roles.CommercialAgent.Code]);

        return new SubmitFatcaDeclarationHandler(
            db,
            new LeadAccessGuard(user, _admin),
            new FatcaSubjectPlanner(_admin, NullLogger<FatcaSubjectPlanner>.Instance),
            user,
            new FrozenClock(Now),
            _publisher);
    }

    private async Task<(Lead Lead, Guid SpouseId)> SeedAsync()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance vie",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, firstName: "Aya", lastName: "Kouassi",
            ownerId: _agent);

        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa"), null, true, Now).Value;

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);

        return (lead, spouse.Id);
    }

    private static SubmitFatcaDeclarationCommand Command(
        Guid leadId, Guid subjectId, string subjectType = "Principal",
        bool usBorn = false, string documentation = "None",
        string? usTin = null, string? evidence = null) => new(
        LeadId: leadId,
        SubjectType: subjectType,
        SubjectId: subjectId,
        Indicia: new FatcaIndiciaInput(BornInTheUnitedStates: usBorn),
        Documentation: documentation,
        UsTin: usTin,
        SelfCertificationDocRef: "s3://self-cert/2026-10-01.pdf",
        EvidenceDocRef: evidence,
        SignedAt: Signed);

    private async Task<List<FatcaDeclaration>> DeclarationsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.FatcaDeclarations.ToListAsync(CancellationToken.None);
    }

    // ── The tenant gate (BNF-B-11) ────────────────────────────────────────

    [Fact]
    public async Task With_FATCA_disabled_nothing_can_be_collected()
    {
        // "Désactivé : aucun champ FATCA exigé, ni exposé, ni calculé". Refusing the write rather
        // than storing and ignoring it: a declaration held by an institution with no obligation is
        // a US TIN kept for no lawful reason.
        FatcaIs(enabled: false);
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(FatcaSubjectPlanner.NotEnabled);
        (await DeclarationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_compliance_settings_outage_refuses_rather_than_guessing()
    {
        // Guessing ON would collect a US TIN with no obligation to hold one; guessing OFF would
        // skip a declaration that is legally required. Neither is acceptable, so neither is done.
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns<Task<ComplianceSettingsDto>>(_ => throw new HttpRequestException("M12 down"));

        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None);

        result.Error.Should().Be(FatcaSubjectPlanner.SettingsUnavailable);
    }

    // ── Who is in scope ───────────────────────────────────────────────────

    [Fact]
    public async Task The_principal_is_always_in_scope()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_co_holder_is_in_scope_when_the_tenant_says_so()
    {
        var (lead, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(
                Command(lead.Id, spouseId, "HouseholdMember"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_co_holder_is_OUT_of_scope_when_the_tenant_says_not()
    {
        // FATCA_FOR_CO_HOLDERS = false means only the principal declares, even though the
        // household exists on the lead.
        FatcaIs(enabled: true, forCoHolders: false);
        var (lead, spouseId) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(
            Command(lead.Id, spouseId, "HouseholdMember"), CancellationToken.None);

        result.Error.Should().Be("FATCA_SUBJECT_NOT_IN_SCOPE");
    }

    [Fact]
    public async Task A_person_who_is_not_of_this_lead_is_refused()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(
                Command(lead.Id, Guid.NewGuid(), "HouseholdMember"), CancellationToken.None))
            .Error.Should().Be("FATCA_SUBJECT_NOT_IN_SCOPE");
    }

    [Fact]
    public async Task A_lead_the_caller_does_not_hold_is_refused()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance vie",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, ownerId: Guid.NewGuid());

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        _admin.GetTeamAgentIdsAsync(_tenant, _agent, Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await using var db = _factory.CreateContext();
        (await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None))
            .Error.Should().Be(LeadAccessGuard.Forbidden);
    }

    // ── Supersession (BNF-B-11) ───────────────────────────────────────────

    [Fact]
    public async Task A_change_of_circumstances_supersedes_rather_than_edits()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        var first = await Handler(db).Handle(
            Command(lead.Id, lead.Id, usBorn: true), CancellationToken.None);

        var second = await Handler(db).Handle(
            Command(lead.Id, lead.Id, usBorn: true,
                    documentation: "FormW9", usTin: "123-45-6789"),
            CancellationToken.None);

        second.IsSuccess.Should().BeTrue(second.Error);

        var all = await DeclarationsAsync();
        all.Should().HaveCount(2, "the history is kept");

        var superseded = all.Single(d => d.Id == first.Value);
        superseded.IsActive.Should().BeFalse();
        superseded.Status.Should().Be(FatcaStatus.IndiciaPending);
        superseded.SupersededByDeclarationId.Should().Be(second.Value);

        var current = all.Single(d => d.Id == second.Value);
        current.IsActive.Should().BeTrue();
        current.Status.Should().Be(FatcaStatus.UsPerson);
    }

    [Fact]
    public async Task Only_one_declaration_per_person_is_ever_active()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        for (var i = 0; i < 3; i++)
            await Handler(db).Handle(
                Command(lead.Id, lead.Id, usBorn: i % 2 == 0), CancellationToken.None);

        var all = await DeclarationsAsync();
        all.Should().HaveCount(3);
        // Asserted on the COUNT, because EF InMemory honours no unique index: the partial index is
        // what enforces this in PostgreSQL, and the handler's ordering is what this checks.
        all.Count(d => d.IsActive).Should().Be(1);
    }

    // ── The compliance event ──────────────────────────────────────────────

    [Fact]
    public async Task A_first_declaration_announces_its_status()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<FatcaStatusChangedIntegrationEvent>(e =>
                e.Status == nameof(FatcaStatus.NonUsPerson)
                && e.PreviousStatus == null
                && !e.RequiresComplianceReview),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Becoming_a_US_person_flags_a_compliance_review()
    {
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(
            Command(lead.Id, lead.Id, usBorn: true), CancellationToken.None);
        _publisher.ClearReceivedCalls();

        await Handler(db).Handle(
            Command(lead.Id, lead.Id, usBorn: true, documentation: "FormW9", usTin: "123-45-6789"),
            CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<FatcaStatusChangedIntegrationEvent>(e =>
                e.Status == nameof(FatcaStatus.UsPerson)
                && e.PreviousStatus == nameof(FatcaStatus.IndiciaPending)
                && e.RequiresComplianceReview),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_certifying_with_an_UNCHANGED_status_announces_nothing()
    {
        // The event drives a compliance notification; re-sending it on an unchanged
        // re-certification is noise a compliance officer learns to ignore.
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None);
        _publisher.ClearReceivedCalls();

        await Handler(db).Handle(Command(lead.Id, lead.Id), CancellationToken.None);

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<FatcaStatusChangedIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_event_carries_no_TIN_and_no_document_reference()
    {
        // An outbox row plus every subscriber's logs is the wrong place for a US taxpayer
        // identification number.
        var (lead, _) = await SeedAsync();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(
            Command(lead.Id, lead.Id, usBorn: true, documentation: "FormW9", usTin: "123-45-6789"),
            CancellationToken.None);

        var payload = _publisher.ReceivedCalls()
            .SelectMany(c => c.GetArguments())
            .OfType<FatcaStatusChangedIntegrationEvent>()
            .Single();

        System.Text.Json.JsonSerializer.Serialize(payload)
            .Should().NotContain("123-45-6789").And.NotContain("s3://");
    }

    // ── Encryption at rest ────────────────────────────────────────────────

    [Fact]
    public async Task The_US_TIN_round_trips_through_the_encrypted_column()
    {
        var (lead, _) = await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            await Handler(db).Handle(
                Command(lead.Id, lead.Id, usBorn: true,
                        documentation: "FormW9", usTin: "123-45-6789"),
                CancellationToken.None);
        }

        var stored = (await DeclarationsAsync()).Single();
        stored.UsTin.Should().Be("123-45-6789");

        var payload = LeadsTestEncryptor.Instance.Encrypt("123-45-6789");
        payload.Should().StartWith("v1:").And.NotContain("123-45-6789");
    }
}
