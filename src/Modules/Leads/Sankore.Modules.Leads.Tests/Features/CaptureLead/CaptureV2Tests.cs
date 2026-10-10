namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The capture contract v2 (BNF-B-18): structured names, an optional household, optional
/// questionnaires — and never health data without the evidence of a consent.
/// </summary>
public sealed class CaptureV2Tests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public CaptureV2Tests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    // ── the v1 / v2 name reconciliation ─────────────────────────────────────

    [Fact]
    public void A_v1_body_with_only_a_full_name_still_maps()
    {
        // Every web form and every integration written before this evolution.
        CaptureLeadEndpoint.ResolveName(Request(fullName: "Awa Ouattara"))
            .Should().Be("Awa Ouattara");
    }

    [Fact]
    public void A_v2_body_derives_the_name_from_the_structured_pair()
    {
        CaptureLeadEndpoint.ResolveName(
                Request(fullName: null, first: "Awa", last: "Ouattara"))
            .Should().Be("Awa Ouattara");
    }

    [Fact]
    public void A_full_name_WINS_when_both_are_present()
    {
        // A prospect whose legal name and usual name differ is ordinary in the UEMOA zone, and
        // recomposing over the top would overwrite what the agent typed.
        CaptureLeadEndpoint.ResolveName(
                Request(fullName: "Mme Awa OUATTARA née KONÉ", first: "Awa", last: "Ouattara"))
            .Should().Be("Mme Awa OUATTARA née KONÉ");
    }

    [Theory]
    [InlineData("Awa", null)]
    [InlineData(null, "Ouattara")]
    public void One_of_the_two_names_alone_is_enough(string? first, string? last)
    {
        // Refusing a capture over a missing surname would lose prospects a web form never asks it
        // of.
        CaptureLeadEndpoint.ResolveName(Request(fullName: null, first: first, last: last))
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_body_with_no_name_at_all_resolves_to_nothing()
    {
        // The endpoint turns this into a 422 before anything is created: inventing a placeholder
        // would put "[sans nom]" on a dispatch queue.
        CaptureLeadEndpoint.ResolveName(Request(fullName: "   ")).Should().BeNull();
    }

    // ── health answers need their consent (BNF-B-09, B-18) ──────────────────

    [Fact]
    public void Answers_with_no_consent_reference_are_refused()
    {
        var result = CaptureHealthConsentCheck.Validate(
        [
            new CaptureQuestionnaire(Guid.NewGuid(), [new CaptureAnswer(Guid.NewGuid(), "Oui")]),
        ]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CaptureHealthConsentCheck.ConsentProofRequired);
    }

    [Fact]
    public void Answers_with_one_are_accepted()
    {
        CaptureHealthConsentCheck.Validate(
        [
            new CaptureQuestionnaire(
                Guid.NewGuid(),
                [new CaptureAnswer(Guid.NewGuid(), "Oui")],
                ConsentProofReference: "doc://consent/1"),
        ]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_EMPTY_questionnaire_needs_no_consent()
    {
        // Not a declaration — a caller saying "this one is expected and not yet answered", which
        // the planner creates on its own. Nothing sensitive travels, so nothing is required.
        CaptureHealthConsentCheck.Validate(
        [
            new CaptureQuestionnaire(Guid.NewGuid(), []),
        ]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_subject_index_past_the_household_is_refused()
    {
        // Accepting it would attach the declaration to the principal silently — attributing one
        // person's health answers to another.
        var result = CaptureHealthConsentCheck.Validate(
            [
                new CaptureQuestionnaire(
                    Guid.NewGuid(),
                    [new CaptureAnswer(Guid.NewGuid(), "Non")],
                    ConsentProofReference: "doc://consent/1",
                    SubjectIndex: 2),
            ],
            householdCount: 1);

        result.Error.Should().Be(CaptureHealthConsentCheck.SubjectOutOfRange);
    }

    // ── the household and the questionnaires land in one transaction ────────

    [Fact]
    public async Task A_v2_capture_records_the_household_with_the_lead()
    {
        // A household recorded by a second request is one that can fail on its own, leaving a lead
        // whose family somebody has to remember to re-enter.
        var result = await CaptureAsync(
            household:
            [
                new CaptureHouseholdMember(
                    "Spouse", "Ouattara", "Awa", SpouseUnionType: "Married"),
                new CaptureHouseholdMember("Child", "Ouattara", "Kofi"),
            ]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstAsync(l => l.Id == result.Value.LeadId, CancellationToken.None);

        lead.HouseholdMembers.Should().HaveCount(2);
        lead.HouseholdMembers.Should().Contain(m =>
            m.Type == HouseholdMemberType.Spouse
            && m.SpouseUnionType == Sankore.Shared.Kernel.ValueObject.SpouseUnionType.Married);
    }

    [Fact]
    public async Task A_member_recorded_at_capture_has_not_been_asked_about_PEP()
    {
        // A stamped declaration would record an answer the form never collected.
        var result = await CaptureAsync(
            household: [new CaptureHouseholdMember("Child", "Ouattara", "Kofi")]);

        await using var db = _factory.CreateContext();
        var member = await db.LeadHouseholdMembers
            .FirstAsync(m => m.LeadId == result.Value.LeadId, CancellationToken.None);

        member.PepDeclaredAt.Should().BeNull();
    }

    [Fact]
    public async Task A_health_questionnaire_lands_with_its_consent_row()
    {
        var templateId = await SeedHealthTemplateAsync();

        var result = await CaptureAsync(
            questionnaires:
            [
                new CaptureQuestionnaire(
                    templateId,
                    [new CaptureAnswer(Guid.NewGuid(), "Non fumeur")],
                    ConsentProofReference: "doc://consent/awa"),
            ]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();

        var response = await db.QualificationResponses
            .FirstAsync(r => r.LeadId == result.Value.LeadId, CancellationToken.None);

        response.IsHealthData.Should().BeTrue("the TEMPLATE decides, not the caller");
        response.SubjectType.Should().Be(QualificationSubjectType.Principal);

        // The consent BEFORE the answers and in the same transaction: a declaration whose consent
        // row failed to write must not exist either.
        var consent = await db.LeadConsents
            .FirstAsync(c => c.LeadId == result.Value.LeadId, CancellationToken.None);

        consent.Type.Should().Be(ConsentType.HealthData);
        consent.ProofReference.Should().Be("doc://consent/awa");
        consent.SubjectType.Should().Be(QualificationSubjectType.Principal);
    }

    [Fact]
    public async Task An_ordinary_questionnaire_writes_no_consent_row()
    {
        var templateId = await SeedHealthTemplateAsync(health: false);

        var result = await CaptureAsync(
            questionnaires:
            [
                new CaptureQuestionnaire(
                    templateId,
                    [new CaptureAnswer(Guid.NewGuid(), "Oui")],
                    ConsentProofReference: "doc://consent/unused"),
            ]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        (await db.LeadConsents.CountAsync(CancellationToken.None)).Should().Be(0,
            "only a health declaration needs one, and the template is what says so");
    }

    [Fact]
    public async Task A_child_health_declaration_is_attached_to_the_CHILD()
    {
        var templateId = await SeedHealthTemplateAsync();

        var result = await CaptureAsync(
            household: [new CaptureHouseholdMember("Child", "Ouattara", "Kofi")],
            questionnaires:
            [
                new CaptureQuestionnaire(
                    templateId,
                    [new CaptureAnswer(Guid.NewGuid(), "Asthme")],
                    ConsentProofReference: "doc://consent/kofi",
                    SubjectIndex: 0),
            ]);

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();

        var member = await db.LeadHouseholdMembers
            .FirstAsync(m => m.LeadId == result.Value.LeadId, CancellationToken.None);

        var response = await db.QualificationResponses
            .FirstAsync(r => r.LeadId == result.Value.LeadId, CancellationToken.None);

        response.SubjectType.Should().Be(QualificationSubjectType.HouseholdMember);
        response.SubjectId.Should().Be(member.Id,
            "the index names the member the capture just created — there is no id to point at yet");
    }

    [Fact]
    public async Task An_unpublished_template_refuses_the_whole_capture()
    {
        // A draft questionnaire's questions can still change, so answers recorded against one are
        // answers to questions nobody can reconstruct — and dropping them silently would tell the
        // caller the declaration was taken.
        var templateId = await SeedHealthTemplateAsync(published: false);

        var result = await CaptureAsync(
            questionnaires:
            [
                new CaptureQuestionnaire(
                    templateId,
                    [new CaptureAnswer(Guid.NewGuid(), "Oui")],
                    ConsentProofReference: "doc://consent/1"),
            ]);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CaptureExtras.TemplateNotPublished);

        await using var db = _factory.CreateContext();
        (await db.Leads.CountAsync(CancellationToken.None)).Should().Be(0,
            "nothing is committed: the lead and the half-built extras share one unit of work");
    }

    [Fact]
    public async Task An_unknown_template_refuses_it_too_and_leaves_no_lead()
    {
        var result = await CaptureAsync(
            questionnaires:
            [
                new CaptureQuestionnaire(
                    Guid.NewGuid(),
                    [new CaptureAnswer(Guid.NewGuid(), "Oui")],
                    ConsentProofReference: "doc://consent/1"),
            ]);

        result.Error.Should().Be(CaptureExtras.TemplateNotFound);

        await using var db = _factory.CreateContext();
        (await db.Leads.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task A_v1_capture_with_no_extras_is_untouched()
    {
        // The regression that matters most: every caller written before this evolution.
        var result = await CaptureAsync();

        result.IsSuccess.Should().BeTrue(result.Error);

        await using var db = _factory.CreateContext();
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstAsync(CancellationToken.None);

        lead.FullName.Should().Be("Awa Ouattara");
        lead.HouseholdMembers.Should().BeEmpty();
        (await db.QualificationResponses.CountAsync(CancellationToken.None)).Should().Be(0);
        (await db.LeadConsents.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private static CaptureLeadRequest Request(
        string? fullName = "Awa Ouattara", string? first = null, string? last = null)
        => new(
            FullName: fullName,
            PhoneNumber: "+2250700000001",
            Source: LeadSource.Web,
            InterestedProduct: "Assurance santé",
            PreferredLanguage: "fr",
            Latitude: 5.3,
            Longitude: -4.0,
            PreferredAgencyId: null,
            FirstName: first,
            LastName: last);

    private async Task<Guid> SeedHealthTemplateAsync(bool health = true, bool published = true)
    {
        await using var db = _factory.CreateContext();

        var template = QualificationTemplate.Create(
            _tenant, health ? "Questionnaire santé" : "BANT", Now,
            // A non-insurance category for the ordinary one: a health questionnaire is only
            // attachable to an insurance category, so the contrast has to be a real product.
            productCategory: health ? ProductCategory.HealthInsurance : ProductCategory.Savings);

        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);

        if (health)
            template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false);

        if (published)
            template.Publish(Now);

        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync(CancellationToken.None);
        return template.Id;
    }

    private async Task<Result<CaptureLeadResult>> CaptureAsync(
        IReadOnlyList<CaptureHouseholdMember>? household = null,
        IReadOnlyList<CaptureQuestionnaire>? questionnaires = null)
    {
        await using var db = _factory.CreateContext();

        var handler = new CaptureLeadHandler(
            db,
            NullLogger<CaptureLeadHandler>.Instance,
            new FixedCaptureClock(Now),
            Substitute.For<MassTransit.IBus>(),
            Substitute.For<Sankore.Modules.Leads.Features.FindDuplicates.IPhoneBlindIndexer>(),
            TestLeadEmailIndexer.Instance,
            Substitute.For<ICustomersModule>(),
            Substitute.For<IAdministrationModule>(),
            _publisher);

        return await handler.Handle(
            new CaptureLeadCommand(
                TenantId: _tenant,
                FullName: "Awa Ouattara",
                PhoneNumber: "+2250700000001",
                Source: LeadSource.Web,
                InterestedProduct: "Assurance santé",
                PreferredLanguage: "fr",
                Latitude: 5.3,
                Longitude: -4.0,
                PreferredAgencyId: null,
                OwnerId: _agent,
                Household: household,
                Questionnaires: questionnaires),
            CancellationToken.None);
    }
}

internal sealed class FixedCaptureClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// The ONE name rule the four inbound paths share (BNF-B-18). Before it, the web ingest required
/// <c>fullName</c> and refused a v2 form that carried a first and last name, while the same
/// payload pulled from a partner's API was accepted — three paths agreeing by coincidence and one
/// disagreeing.
/// </summary>
public sealed class LeadNameResolverTests
{
    [Fact]
    public void A_supplied_full_name_wins()
    {
        // A caller that sent one meant it: a prospect whose legal and usual names differ is
        // ordinary here, and recomposing over the top would overwrite what was captured.
        Sankore.Modules.Leads.Features.CaptureLead.LeadNameResolver
            .Resolve("Mme Awa OUATTARA née KONÉ", "Awa", "Ouattara")
            .Should().Be("Mme Awa OUATTARA née KONÉ");
    }

    [Theory]
    [InlineData(null, "Awa", "Ouattara", "Awa Ouattara")]
    [InlineData("", "Awa", "Ouattara", "Awa Ouattara")]
    [InlineData("   ", "Awa", "Ouattara", "Awa Ouattara")]
    [InlineData(null, "Awa", null, "Awa")]
    [InlineData(null, null, "Ouattara", "Ouattara")]
    [InlineData("  Awa Ouattara  ", null, null, "Awa Ouattara")]
    public void The_structured_pair_is_derived_and_everything_is_trimmed(
        string? full, string? first, string? last, string expected)
        => Sankore.Modules.Leads.Features.CaptureLead.LeadNameResolver
            .Resolve(full, first, last)
            .Should().Be(expected);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("  ", "  ", "  ")]
    public void A_payload_with_no_name_resolves_to_nothing(string? full, string? first, string? last)
    {
        // Every caller turns this into a refusal rather than a placeholder — a lead called
        // "[sans nom]" on a dispatch queue is worse than a payload somebody resends.
        Sankore.Modules.Leads.Features.CaptureLead.LeadNameResolver
            .Resolve(full, first, last)
            .Should().BeNull();
    }
}
