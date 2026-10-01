namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Events;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public class ClientAggregateTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid AdvisorId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    // ── Creation ────────────────────────────────────────────────────────────

    [Fact]
    public void CreateIndividual_should_start_pending_kyc_with_a_display_name_and_a_creation_event()
    {
        var client = NewIndividual();

        client.Status.Should().Be(ClientStatus.PendingKyc);
        client.Type.Should().Be(ClientType.Individual);
        client.KycStatus.Should().Be(KycStatus.NotStarted);
        client.RiskLevel.Should().Be(RiskLevel.Unknown);
        client.DisplayName.Should().Be("Awa Ouattara");
        // Lower-case on purpose: email template locales are stored lower-case and resolved in
        // PostgreSQL, where equality is case-sensitive. "FR" used to be the default here and
        // matched no template at all.
        client.PreferredLanguage.Should().Be(Client.DefaultLanguageCode).And.Be("fr");
        client.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        client.IsReadOnly.Should().BeFalse();
        client.DomainEvents.OfType<ClientCreatedDomainEvent>().Should().ContainSingle()
            .Which.ClientId.Should().Be(client.Id);
        client.StatusHistory.Should().ContainSingle()
            .Which.OldStatus.Should().BeNull();
    }

    [Fact]
    public void CreateLegal_should_use_the_legal_name_as_display_name()
    {
        var client = NewLegal();

        client.Type.Should().Be(ClientType.Legal);
        client.Status.Should().Be(ClientStatus.PendingKyc);
        client.DisplayName.Should().Be("Ets Sankore Négoce");
        client.LegalFormCode.Should().Be("SARL");
    }

    [Fact]
    public void CreateIndividual_should_refuse_a_blank_last_name()
    {
        var act = () => NewIndividual(lastName: "  ");

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>()
            .Which.MessageKey.Should().Be("Client.LastName.Required");
    }

    // ── KYC ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyKycValidated_should_activate_a_pending_client_and_record_the_transition()
    {
        var client = NewIndividual();
        var at = DateTimeOffset.UtcNow;

        var result = client.ApplyKycValidated(at, ActorId);

        result.IsSuccess.Should().BeTrue();
        client.Status.Should().Be(ClientStatus.Active);
        client.KycStatus.Should().Be(KycStatus.Approved);
        client.KycUpdatedAt.Should().Be(at);
        client.StatusHistory.Should().HaveCount(2);
        client.StatusHistory.Last().OldStatus.Should().Be(ClientStatus.PendingKyc);
        client.StatusHistory.Last().NewStatus.Should().Be(ClientStatus.Active);
        client.DomainEvents.OfType<ClientStatusChangedDomainEvent>().Should().ContainSingle()
            .Which.To.Should().Be(ClientStatus.Active);
    }

    [Fact]
    public void ApplyKycValidated_should_only_refresh_the_kyc_status_of_a_suspended_client()
    {
        var client = ActiveIndividual();
        client.Suspend("Compte gelé", ActorId);
        client.ApplyKycRejected("Pièce illisible", DateTimeOffset.UtcNow, ActorId);
        var historyCount = client.StatusHistory.Count;

        var result = client.ApplyKycValidated(DateTimeOffset.UtcNow, ActorId);

        result.IsSuccess.Should().BeTrue();
        client.KycStatus.Should().Be(KycStatus.Approved);
        client.Status.Should().Be(ClientStatus.Suspended);
        client.StatusHistory.Should().HaveCount(historyCount);
    }

    [Fact]
    public void ApplyKycValidated_should_only_refresh_the_kyc_status_of_an_archived_client()
    {
        var client = NewIndividual();
        client.Archive("Doublon", ActorId);
        var historyCount = client.StatusHistory.Count;

        client.ApplyKycValidated(DateTimeOffset.UtcNow, ActorId);

        client.KycStatus.Should().Be(KycStatus.Approved);
        client.Status.Should().Be(ClientStatus.Archived);
        client.StatusHistory.Should().HaveCount(historyCount);
    }

    [Fact]
    public void ApplyKycRejected_should_reject_a_pending_client_and_keep_the_reason()
    {
        var client = NewIndividual();

        client.ApplyKycRejected("Pièce expirée", DateTimeOffset.UtcNow, ActorId);

        client.Status.Should().Be(ClientStatus.KycRejected);
        client.KycStatus.Should().Be(KycStatus.Rejected);
        client.KycRejectionReason.Should().Be("Pièce expirée");
    }

    [Fact]
    public void ApplyKycRejected_without_a_reason_should_fail()
    {
        var client = NewIndividual();

        client.ApplyKycRejected("  ", DateTimeOffset.UtcNow, ActorId)
            .Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    // ── Suspend / reactivate ────────────────────────────────────────────────

    [Fact]
    public void Suspend_without_a_reason_should_fail_with_reason_required()
    {
        var client = ActiveIndividual();

        var result = client.Suspend("   ", ActorId);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
        client.Status.Should().Be(ClientStatus.Active);
    }

    [Fact]
    public void Suspend_a_client_that_is_not_active_should_fail_with_invalid_status_transition()
    {
        var client = NewIndividual();   // still PendingKyc

        var result = client.Suspend("Fraude", ActorId);

        result.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
        client.Status.Should().Be(ClientStatus.PendingKyc);
    }

    [Fact]
    public void Reactivate_should_return_to_active_when_kyc_is_approved()
    {
        var client = ActiveIndividual();
        client.Suspend("Enquête interne", ActorId);

        var result = client.Reactivate(ActorId);

        result.IsSuccess.Should().BeTrue();
        client.Status.Should().Be(ClientStatus.Active);
    }

    [Fact]
    public void Reactivate_should_return_to_pending_kyc_when_kyc_is_not_approved()
    {
        var client = ActiveIndividual();
        client.Suspend("Enquête interne", ActorId);
        client.ApplyKycRejected("Pièce non conforme", DateTimeOffset.UtcNow, ActorId);

        var result = client.Reactivate(ActorId);

        result.IsSuccess.Should().BeTrue();
        client.Status.Should().Be(ClientStatus.PendingKyc);
    }

    [Fact]
    public void Reactivate_a_client_that_is_not_suspended_should_fail()
    {
        var client = ActiveIndividual();

        client.Reactivate(ActorId).Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }

    // ── Archive & read-only ─────────────────────────────────────────────────

    [Fact]
    public void Archive_should_stamp_the_archive_date_and_make_the_client_read_only()
    {
        var client = ActiveIndividual();

        var result = client.Archive("Client décédé", ActorId);

        result.IsSuccess.Should().BeTrue();
        client.Status.Should().Be(ClientStatus.Archived);
        client.ArchivedAt.Should().NotBeNull();
        client.ArchiveReason.Should().Be("Client décédé");
        client.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Archive_without_a_reason_should_fail()
    {
        var client = ActiveIndividual();

        client.Archive("", ActorId).Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public void Every_mutation_of_an_archived_client_should_fail_with_client_read_only()
    {
        var client = ActiveIndividual();
        var phone = client.ContactPoints.First().Id;
        client.Archive("Dossier clos", ActorId);

        client.Suspend("Fraude", ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
        client.Reactivate(ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
        client.Archive("Encore", ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
        client.AssignAdvisor(Guid.NewGuid(), ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
        client.TransferToAgency(Guid.NewGuid(), "AG2", true, ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
        client.UpdateNonSensitive("Tailleur", null, null, null, null, null, ActorId).Error
            .Should().Be(CustomerErrors.ClientReadOnly);
        client.UpdateSensitiveIdentity("Awa", "Kone", null, null, null, null, null, null, ActorId).Error
            .Should().Be(CustomerErrors.ClientReadOnly);
        client.UpdateLegalIdentity("X", null, null, null, null, null, ActorId).Error
            .Should().Be(CustomerErrors.ClientReadOnly);
        client.CloseContactPoint(phone, ActorId, DateTimeOffset.UtcNow).Error
            .Should().Be(CustomerErrors.ClientReadOnly);
        client.PromoteContactPointToPrimary(phone, ActorId).Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public void MarkMerged_should_refuse_the_client_itself_and_a_second_merge()
    {
        var client = ActiveIndividual();
        var survivorId = Guid.NewGuid();

        client.MarkMerged(client.Id, ActorId).Error.Should().Be(CustomerErrors.SameClientMergeForbidden);
        client.MarkMerged(survivorId, ActorId).IsSuccess.Should().BeTrue();
        client.Status.Should().Be(ClientStatus.Merged);
        client.MergedIntoId.Should().Be(survivorId);
        client.IsReadOnly.Should().BeTrue();
        client.MarkMerged(Guid.NewGuid(), ActorId).Error.Should().Be(CustomerErrors.ClientAlreadyMerged);
        client.DomainEvents.OfType<ClientMergedDomainEvent>().Should().ContainSingle();
    }

    // ── Ownership ───────────────────────────────────────────────────────────

    [Fact]
    public void TransferToAgency_should_clear_the_advisor_when_it_is_not_kept()
    {
        var client = ActiveIndividual();
        var targetAgencyId = Guid.NewGuid();
        client.AdvisorUserId.Should().Be(AdvisorId);

        var result = client.TransferToAgency(targetAgencyId, "AG2", keepAdvisor: false, ActorId);

        result.IsSuccess.Should().BeTrue();
        client.AgencyId.Should().Be(targetAgencyId);
        client.AgencyCode.Should().Be("AG2");
        client.AdvisorUserId.Should().BeNull();
        client.DomainEvents.OfType<ClientTransferredDomainEvent>().Should().ContainSingle()
            .Which.ToAgencyId.Should().Be(targetAgencyId);
    }

    [Fact]
    public void TransferToAgency_should_keep_the_advisor_when_asked_to()
    {
        var client = ActiveIndividual();

        client.TransferToAgency(Guid.NewGuid(), "AG2", keepAdvisor: true, ActorId);

        client.AdvisorUserId.Should().Be(AdvisorId);
    }

    // ── Sensitive identity ──────────────────────────────────────────────────

    [Fact]
    public void UpdateSensitiveIdentity_should_recompute_the_display_name_and_announce_the_changed_fields()
    {
        var client = ActiveIndividual();

        var result = client.UpdateSensitiveIdentity(
            "Aminata", "Kone", null, IdentityDocumentType.Passport, "enc:P4242", "bi-p4242", null, null, ActorId);

        result.IsSuccess.Should().BeTrue();
        client.DisplayName.Should().Be("Aminata Kone");
        client.IdentityDocumentNumberBlindIndex.Should().Be("bi-p4242");

        var changed = client.DomainEvents.OfType<ClientSensitiveFieldsChangedDomainEvent>().Should().ContainSingle().Subject;
        changed.Fields.Should().Contain(nameof(SensitiveField.IdentityDocumentNumber));
        changed.Fields.Should().Contain("FirstName").And.Contain("LastName");
    }

    // ── Contact points ──────────────────────────────────────────────────────

    [Fact]
    public void The_first_contact_point_of_a_type_should_become_primary_on_its_own()
    {
        var client = NewIndividual();

        var email = client.AddContactPoint(
            ContactPointType.Email, "enc:mail", "bi-mail", "Perso", isPrimary: false, DateTimeOffset.UtcNow, ActorId);

        email.IsPrimary.Should().BeTrue();
        email.IsActive.Should().BeTrue();
    }

    [Fact]
    public void CloseContactPoint_should_refuse_to_remove_the_last_active_phone()
    {
        var client = ActiveIndividual();
        var onlyPhone = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone);

        var result = client.CloseContactPoint(onlyPhone.Id, ActorId, DateTimeOffset.UtcNow);

        result.Error.Should().Be(CustomerErrors.LastPhoneRequired);
        onlyPhone.IsActive.Should().BeTrue();
    }

    [Fact]
    public void CloseContactPoint_should_close_a_phone_when_another_one_remains()
    {
        var client = ActiveIndividual();
        var first = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone);
        var second = client.AddContactPoint(
            ContactPointType.Phone, "enc:phone2", "bi-phone2", "Bureau", false, DateTimeOffset.UtcNow, ActorId);

        var result = client.CloseContactPoint(first.Id, ActorId, DateTimeOffset.UtcNow);

        result.IsSuccess.Should().BeTrue();
        first.IsActive.Should().BeFalse();
        first.ValidTo.Should().NotBeNull();
        // The type must never be left without a primary.
        second.IsPrimary.Should().BeTrue();
        client.ContactPoints.Should().HaveCount(2, "closing historizes, it never deletes");
    }

    [Fact]
    public void CloseContactPoint_should_report_an_unknown_contact_point()
    {
        var client = ActiveIndividual();

        client.CloseContactPoint(Guid.NewGuid(), ActorId, DateTimeOffset.UtcNow)
            .Error.Should().Be(CustomerErrors.ContactPointNotFound);
    }

    [Fact]
    public void PromoteContactPointToPrimary_should_demote_the_previous_primary_of_that_type_only()
    {
        var client = ActiveIndividual();
        var firstPhone = client.ContactPoints.Single(cp => cp.Type == ContactPointType.Phone);
        var secondPhone = client.AddContactPoint(
            ContactPointType.Phone, "enc:phone2", "bi-phone2", "Bureau", false, DateTimeOffset.UtcNow, ActorId);
        var email = client.AddContactPoint(
            ContactPointType.Email, "enc:mail", "bi-mail", "Perso", true, DateTimeOffset.UtcNow, ActorId);

        var result = client.PromoteContactPointToPrimary(secondPhone.Id, ActorId);

        result.IsSuccess.Should().BeTrue();
        secondPhone.IsPrimary.Should().BeTrue();
        firstPhone.IsPrimary.Should().BeFalse();
        email.IsPrimary.Should().BeTrue("promoting a phone must not touch the primary e-mail");
    }

    // ── Misc setters ────────────────────────────────────────────────────────

    [Fact]
    public void Anonymize_should_wipe_identity_and_close_every_contact_point()
    {
        var client = ActiveIndividual();
        var at = DateTimeOffset.UtcNow;

        client.Anonymize(ActorId, at);

        client.IsAnonymized.Should().BeTrue();
        client.FirstName.Should().BeNull();
        client.LastName.Should().BeNull();
        client.EncryptedIdentityDocumentNumber.Should().BeNull();
        client.IdentityDocumentNumberBlindIndex.Should().BeNull();
        client.PhoneticKeyPrimary.Should().BeNull();
        client.DisplayName.Should().Be($"ANONYMIZED-{client.ClientNumber}");
        client.ContactPoints.Should().OnlyContain(cp => !cp.IsActive);
    }

    [Fact]
    public void SetPhoneticKeys_and_scores_should_be_stored_as_given()
    {
        var client = NewIndividual();
        var at = DateTimeOffset.UtcNow;

        client.SetPhoneticKeys("WTR", "A");
        client.SetSegment("PREMIUM", at);
        client.SetLoyaltyScore(84, provisional: true, at);
        client.RecomputeDependentsCount(-3);
        client.ApplyRiskLevel(RiskLevel.High, at);

        client.PhoneticKeyPrimary.Should().Be("WTR");
        client.PhoneticKeySecondary.Should().Be("A");
        client.SegmentCode.Should().Be("PREMIUM");
        client.SegmentAssignedAt.Should().Be(at);
        client.LoyaltyScore.Should().Be(84);
        client.LoyaltyScoreProvisional.Should().BeTrue();
        client.DependentsCount.Should().Be(0, "a negative dependents count is clamped");
        client.RiskLevel.Should().Be(RiskLevel.High);
    }

    // ── Builders ────────────────────────────────────────────────────────────

    private static Client NewIndividual(string firstName = "Awa", string lastName = "Ouattara") =>
        Client.CreateIndividual(
            TenantId,
            "AG1-2026-000001",
            AgencyId,
            "AG1",
            AdvisorId,
            firstName,
            lastName,
            null,
            Gender.Female,
            "enc:1990-04-12",
            "bi-dob",
            "Abidjan",
            "CI",
            MaritalStatus.Single,
            "Ibrahim",
            "Fatoumata",
            "Couturière",
            null,
            null,
            null,
            "FR",
            IdentityDocumentType.NationalIdCard,
            "enc:CI4242",
            "bi-ci4242",
            new DateOnly(2020, 1, 10),
            new DateOnly(2030, 1, 9),
            ActorId);

    /// <summary>A KYC-approved, active client carrying exactly one (primary) phone.</summary>
    private static Client ActiveIndividual()
    {
        var client = NewIndividual();
        client.SetPhoneticKeys("WTR", "A");
        client.AddContactPoint(
            ContactPointType.Phone, "enc:+2250707000001", "bi-phone1", "Mobile", true, DateTimeOffset.UtcNow, ActorId);
        client.ApplyKycValidated(DateTimeOffset.UtcNow, ActorId);
        return client;
    }

    private static Client NewLegal() =>
        Client.CreateLegal(
            TenantId,
            "AG1-2026-000002",
            AgencyId,
            "AG1",
            AdvisorId,
            "Ets Sankore Négoce",
            "SARL",
            "enc:RC-ABJ-2019-B-1234",
            "bi-rc",
            "enc:TAX-99",
            new DateOnly(2019, 6, 1),
            "FR",
            ActorId);
}
