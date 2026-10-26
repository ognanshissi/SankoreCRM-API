namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the protection of health answers (BNF-B-09): the consent gate, the respondent rule, the
/// permission gate and the encryption at rest.
/// </summary>
public sealed class HealthProtectionTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public HealthProtectionTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Held() => Lead.Capture(
        tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
        source: LeadSource.Agency, interestedProduct: "Assurance santé",
        preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
        clock: TimeProvider.System, firstName: "Aya", lastName: "Kouassi",
        ownerId: Guid.NewGuid());

    private static QualificationResponse HealthResponse(Guid tenant, Guid leadId, bool health = true)
    {
        var response = QualificationResponse.Create(
            tenantId: tenant, leadId: leadId, templateId: Guid.NewGuid(),
            answeredBy: Guid.NewGuid(), clock: TimeProvider.System,
            answers: [(Guid.NewGuid(), "Oui, depuis dix ans")],
            computedScore: 0, questionnaireVersion: 1,
            subjectType: QualificationSubjectType.Principal, subjectId: leadId,
            isHealthData: health);

        return response;
    }

    // ── Sent to the insurer ───────────────────────────────────────────────

    [Fact]
    public void An_answer_sent_to_the_insurer_with_a_subscription_can_no_longer_be_edited()
    {
        // The insurer holds the declaration the client signed; a later edit here would make the
        // CRM disagree with the contract.
        var response = HealthResponse(Guid.NewGuid(), Guid.NewGuid());
        response.MarkTransmitted(DateTimeOffset.UtcNow);

        var edited = response.Answer(
            [(Guid.NewGuid(), "Non")], Guid.NewGuid(), respondentId: null, computedScore: 0, DateTimeOffset.UtcNow);

        edited.IsFailure.Should().BeTrue();
        edited.Error.Should().Be("QUALIFICATION_RESPONSE_TRANSMITTED");
        response.GetAnswers().Single().Value.Should().Be("Oui, depuis dix ans");
    }

    // ── The consent gate ──────────────────────────────────────────────────

    [Fact]
    public async Task Without_consent_a_health_answer_is_refused()
    {
        var lead = Held();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);

        var guard = new HealthConsentGuard(db);
        var result = await guard.EnsureConsentAsync(
            lead.Id, QualificationSubjectType.Principal, lead.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HealthConsentGuard.Missing);
    }

    [Fact]
    public async Task With_an_active_consent_for_that_person_it_is_allowed()
    {
        var lead = Held();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.LeadConsents.Add(LeadConsent.Create(
            _tenant, lead.Id, ConsentType.HealthData, ConsentChannel.Paper,
            Guid.NewGuid(), TimeProvider.System, "signature-2026-10",
            QualificationSubjectType.Principal, lead.Id));
        await db.SaveChangesAsync(CancellationToken.None);

        (await new HealthConsentGuard(db).EnsureConsentAsync(
                lead.Id, QualificationSubjectType.Principal, lead.Id, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_consent_for_the_principal_does_NOT_cover_a_child()
    {
        // Per person, explicitly (BNF-B-09). One signature does not consent for the whole family.
        var lead = Held();
        var childId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.LeadConsents.Add(LeadConsent.Create(
            _tenant, lead.Id, ConsentType.HealthData, ConsentChannel.Paper,
            Guid.NewGuid(), TimeProvider.System, "signature",
            QualificationSubjectType.Principal, lead.Id));
        await db.SaveChangesAsync(CancellationToken.None);

        (await new HealthConsentGuard(db).EnsureConsentAsync(
                lead.Id, QualificationSubjectType.HouseholdMember, childId, CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task A_WITHDRAWN_consent_stops_further_collection()
    {
        // The whole point of being able to withdraw one.
        var lead = Held();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        var consent = LeadConsent.Create(
            _tenant, lead.Id, ConsentType.HealthData, ConsentChannel.Paper,
            Guid.NewGuid(), TimeProvider.System, "signature",
            QualificationSubjectType.Principal, lead.Id);
        consent.Withdraw(Guid.NewGuid(), TimeProvider.System, "retrait demandé");
        db.LeadConsents.Add(consent);
        await db.SaveChangesAsync(CancellationToken.None);

        (await new HealthConsentGuard(db).EnsureConsentAsync(
                lead.Id, QualificationSubjectType.Principal, lead.Id, CancellationToken.None))
            .IsFailure.Should().BeTrue();
    }

    // ── The minor respondent rule (BNF-B-08) ──────────────────────────────

    [Fact]
    public void A_minor_needs_a_respondent()
    {
        var lead = Held();
        var guard = new HealthConsentGuard(_factory.CreateContext());

        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(),
                respondentId: null, minorAge: 8)
            .Error.Should().Be(HealthConsentGuard.RespondentRequired);
    }

    [Fact]
    public void A_minors_respondent_must_be_the_principal_or_the_spouse()
    {
        var lead = Held();
        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa"), null, true, Now);
        var spouseId = lead.ActiveHouseholdMembers.Single(m => m.Type == HouseholdMemberType.Spouse).Id;

        var guard = new HealthConsentGuard(_factory.CreateContext());

        // The principal is identified by the lead's own id.
        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(), lead.Id, 8)
            .IsSuccess.Should().BeTrue();

        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(), spouseId, 8)
            .IsSuccess.Should().BeTrue();

        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(), Guid.NewGuid(), 8)
            .Error.Should().Be(HealthConsentGuard.RespondentInvalid);
    }

    [Fact]
    public void An_adult_member_answers_for_themselves()
    {
        var lead = Held();
        var guard = new HealthConsentGuard(_factory.CreateContext());

        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(),
                respondentId: null, minorAge: 34)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_age_does_not_trigger_the_rule()
    {
        // BNF-B-01 keeps the birth date optional. Refusing here would block a declaration over a
        // field the capture never required; the completeness check reports the missing date.
        var lead = Held();
        var guard = new HealthConsentGuard(_factory.CreateContext());

        guard.EnsureRespondentValid(
                lead, QualificationSubjectType.HouseholdMember, Guid.NewGuid(),
                respondentId: null, minorAge: null)
            .IsSuccess.Should().BeTrue();
    }

    // ── The permission gate (BNF-B-09) ────────────────────────────────────

    [Fact]
    public void Health_answers_are_refused_without_the_permission()
    {
        var response = HealthResponse(_tenant, Guid.NewGuid());

        var result = HealthAnswerAccess.Read(response, callerHoldsHealthPermission: false);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HealthAnswerAccess.Forbidden);
    }

    [Fact]
    public void Health_answers_are_returned_with_the_permission()
    {
        var response = HealthResponse(_tenant, Guid.NewGuid());

        var result = HealthAnswerAccess.Read(response, callerHoldsHealthPermission: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Answers.Should().ContainSingle()
            .Which.Value.Should().Be("Oui, depuis dix ans");
    }

    [Fact]
    public void A_NON_health_response_needs_no_health_permission()
    {
        // The gate is about health data, not about questionnaires — a BANT answer is ordinary
        // lead information.
        var response = HealthResponse(_tenant, Guid.NewGuid(), health: false);

        HealthAnswerAccess.Read(response, callerHoldsHealthPermission: false)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_purged_response_is_distinguishable_from_an_empty_one()
    {
        // The caller must be able to tell "the retention period expired" from "nobody answered".
        var response = HealthResponse(_tenant, Guid.NewGuid());
        response.Purge();

        var result = HealthAnswerAccess.Read(response, callerHoldsHealthPermission: true);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HealthAnswerAccess.Purged);
    }

    // ── Encryption at rest ────────────────────────────────────────────────

    [Fact]
    public async Task Health_answers_round_trip_through_the_encrypted_column()
    {
        var lead = Held();
        var response = HealthResponse(_tenant, lead.Id);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            write.QualificationResponses.Add(response);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.QualificationResponses.SingleAsync(CancellationToken.None);

        stored.IsHealthData.Should().BeTrue();
        stored.GetAnswers().Should().ContainSingle()
            .Which.Value.Should().Be("Oui, depuis dix ans");

        // The ciphertext is non-deterministic, which is what makes the column unsearchable — and
        // why BNF-B-09 says a health answer gets no blind index.
        var first  = LeadsTestEncryptor.Instance.Encrypt("Oui, depuis dix ans");
        var second = LeadsTestEncryptor.Instance.Encrypt("Oui, depuis dix ans");
        first.Should().StartWith("v1:").And.NotContain("depuis dix ans");
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task A_non_health_response_stays_in_the_plain_jsonb_column()
    {
        // Two columns, two meanings: a mis-classified response cannot put clear health answers
        // into a jsonb column somebody later decides to index.
        var lead = Held();
        var response = HealthResponse(_tenant, lead.Id, health: false);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            write.QualificationResponses.Add(response);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.QualificationResponses.SingleAsync(CancellationToken.None);

        stored.IsHealthData.Should().BeFalse();
        stored.GetAnswers().Should().ContainSingle();
    }

    [Fact]
    public async Task Purging_blanks_the_answers_and_keeps_the_row()
    {
        // The row says a declaration was made, by whom and when; the content is what a retention
        // period limits (BNF-B-09).
        var lead = Held();
        var response = HealthResponse(_tenant, lead.Id);
        response.MarkTransmitted(Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            write.QualificationResponses.Add(response);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var purge = _factory.CreateContext())
        {
            var tracked = await purge.QualificationResponses
                .AsTracking().SingleAsync(CancellationToken.None);
            tracked.Purge();
            await purge.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.QualificationResponses.SingleAsync(CancellationToken.None);

        stored.IsPurged.Should().BeTrue();
        stored.GetAnswers().Should().BeEmpty();
        stored.AnsweredAt.Should().NotBeNull("the row still records that a declaration was made");
        stored.TransmittedAt.Should().NotBeNull();
    }
}
