namespace Sankore.Modules.Administration.Tests.Features;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Credentials;
using Sankore.Modules.Administration.Features.Credentials.CloseCredential;
using Sankore.Modules.Administration.Features.Credentials.ExpiringCredentials;
using Sankore.Modules.Administration.Features.Credentials.IssueCredential;
using Sankore.Modules.Administration.Features.Credentials.ListCredentials;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Compliance;
using Xunit;

/// <summary>
/// The register of professional cards (CIMA articles 503, 510, 732) and the eligibility answer the
/// dispatching and subscription guards read from it.
///
/// <para>
/// The behaviour worth pinning is that the register is HISTORISED. A card expires and is renewed,
/// and the question a national directorate asks is not "is this agent licensed today" but "was
/// this agent licensed on the day they presented this contract" — which a table that overwrote
/// its rows could not answer at all.
/// </para>
/// </summary>
public sealed class DistributionCredentialTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public DistributionCredentialTests() => _factory = new TestAdminDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenant); user.TeamTenantId.Returns(_tenant);
        user.Id.Returns(_actor);
        return user;
    }

    /// <summary>
    /// A real AES/HMAC pair over a zero key. Real primitives rather than a passthrough double:
    /// the handler stores ciphertext and matches on a blind index, and a stub that returned the
    /// clear value would make the duplicate-number case pass for the wrong reason.
    /// </summary>
    private static (IFieldEncryptor Encryptor, IBlindIndexer Indexer) Crypto()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FieldProtectionOptions
        {
            SectionName = "Administration",
            FieldEncryptionKey = Convert.ToBase64String(new byte[32]),
            BlindIndexKey = Convert.ToBase64String(new byte[32]),
        });

        return (new AesGcmFieldEncryptor(options), new HmacBlindIndexer(options));
    }

    private async Task<Guid> SeedUserAsync()
    {
        await using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenant, _agencyId, "Awa", "Ouattara", "awa@sankore.ci");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Sankore.Shared.Kernel.Result<CredentialDto>> IssueAsync(
        Guid userId,
        string cardNumber = "CP-2026-000142",
        DateOnly? issuedOn = null,
        DateOnly? expiresOn = null,
        DateOnly? indemnityExpiresOn = null,
        int? trainingHours = 48,
        string? supersedeReason = null)
    {
        await using var db = _factory.CreateContext();
        var (encryptor, indexer) = Crypto();

        return await new IssueCredentialHandler(
                db, encryptor, indexer, User(), new FixedClockForCredentials(Now))
            .Handle(
                new IssueCredentialCommand(
                    UserId: userId,
                    Category: nameof(DistributionCredentialCategory.CounterPresenter),
                    CardNumber: cardNumber,
                    IssuingAuthority: "Direction Nationale des Assurances",
                    IssuedOn: issuedOn ?? new DateOnly(2026, 1, 1),
                    ExpiresOn: expiresOn ?? new DateOnly(2027, 1, 1),
                    IndemnityInsurer: "NSIA",
                    IndemnityPolicyReference: "RCPRO-77",
                    IndemnityExpiresOn: indemnityExpiresOn ?? new DateOnly(2027, 1, 1),
                    TrainingHours: trainingHours,
                    TrainingCompletedOn: new DateOnly(2025, 11, 20),
                    SupersedeReason: supersedeReason),
                CancellationToken.None);
    }

    private async Task<DistributionEligibilitySnapshot> EligibilityAsync(
        Guid userId, DateOnly? asOf = null)
    {
        await using var db = _factory.CreateContext();

        return await new AdministrationModuleFacade(db, new ProductCatalogCache())
            .GetDistributionEligibilityAsync(_tenant, userId, asOf ?? Today, CancellationToken.None);
    }

    // ── issuing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_is_recorded_and_makes_its_holder_eligible()
    {
        var userId = await SeedUserAsync();

        var issued = await IssueAsync(userId);

        issued.IsSuccess.Should().BeTrue();
        issued.Value!.IsCurrent.Should().BeTrue();
        issued.Value.IsValidToday.Should().BeTrue();

        (await EligibilityAsync(userId)).Status.Should().Be(DistributionEligibility.Valid);
    }

    [Fact]
    public async Task The_card_number_is_never_returned_and_never_stored_in_clear()
    {
        var userId = await SeedUserAsync();
        const string number = "CP-2026-000142";

        await IssueAsync(userId, cardNumber: number);

        // Not on the DTO at all — the type is the guarantee.
        typeof(CredentialDto).GetProperties().Select(p => p.Name)
            .Should().NotContain("CardNumber");

        await using var db = _factory.CreateContext();
        var stored = await db.DistributionCredentials.FirstAsync();

        stored.EncryptedCardNumber.Should().NotContain(number);
        stored.EncryptedCardNumber.Should().StartWith("v1:");
        stored.CardNumberBlindIndex.Should().HaveLength(64);
    }

    [Fact]
    public async Task An_unknown_user_is_refused()
    {
        var result = await IssueAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CredentialErrors.UserNotFound);
    }

    [Fact]
    public async Task A_term_that_ends_before_it_starts_is_refused_by_the_aggregate()
    {
        // The invariant throws rather than returning a Result: a credential that was never valid
        // for a day has no reason to exist, and its holder would be silently excluded from every
        // insurance dispatch.
        var act = () => DistributionCredential.Issue(
            _tenant, Guid.NewGuid(), DistributionCredentialCategory.Broker,
            "cipher", new string('a', 64), "DNA",
            issuedOn: new DateOnly(2026, 6, 1), expiresOn: new DateOnly(2026, 1, 1),
            createdBy: _actor, now: Now);

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>();
    }

    [Fact]
    public async Task One_card_number_cannot_be_current_for_two_holders()
    {
        // A number belongs to one person. Two holders sharing one is either a typo or a licence
        // being passed around, and both are things a register must refuse to record.
        var first = await SeedUserAsync();
        var second = await SeedUserAsync();

        (await IssueAsync(first, cardNumber: "CP-SHARED")).IsSuccess.Should().BeTrue();

        var clash = await IssueAsync(second, cardNumber: "CP-SHARED");

        clash.IsFailure.Should().BeTrue();
        clash.Error.Should().Be(CredentialErrors.DuplicateCardNumber);
    }

    // ── renewal: the history is the point ───────────────────────────────────

    [Fact]
    public async Task A_renewal_closes_the_previous_card_instead_of_editing_it()
    {
        var userId = await SeedUserAsync();
        await IssueAsync(userId, cardNumber: "CP-OLD");

        var renewed = await IssueAsync(
            userId,
            cardNumber: "CP-NEW",
            issuedOn: new DateOnly(2026, 10, 1),
            expiresOn: new DateOnly(2028, 10, 1),
            supersedeReason: "Renouvellement annuel");

        renewed.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var rows = await db.DistributionCredentials.OrderBy(c => c.ValidFrom).ToListAsync();

        rows.Should().HaveCount(2, "the old card is kept, not overwritten");
        rows[0].IsCurrent.Should().BeFalse();
        rows[0].ClosedReason.Should().Be("Renouvellement annuel");
        rows[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task A_renewal_with_no_reason_is_refused()
    {
        // Without it the register cannot tell a lapse from a withdrawal, which is the first
        // thing a controller asks.
        var userId = await SeedUserAsync();
        await IssueAsync(userId, cardNumber: "CP-OLD");

        var renewed = await IssueAsync(userId, cardNumber: "CP-NEW", supersedeReason: null);

        renewed.IsFailure.Should().BeTrue();
        renewed.Error.Should().Be(CredentialErrors.SupersedeReasonRequired);
    }

    [Fact]
    public async Task A_holder_may_reuse_their_OWN_number_on_a_renewal()
    {
        // The duplicate rule is about two PEOPLE sharing a number. A reissued card often carries
        // the same one, and refusing that would make a renewal impossible to record.
        var userId = await SeedUserAsync();
        await IssueAsync(userId, cardNumber: "CP-SAME");

        var renewed = await IssueAsync(
            userId, cardNumber: "CP-SAME", supersedeReason: "Réédition");

        renewed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_history_lists_the_current_card_first()
    {
        var userId = await SeedUserAsync();
        await IssueAsync(userId, cardNumber: "CP-OLD");
        await IssueAsync(userId, cardNumber: "CP-NEW", supersedeReason: "Renouvellement");

        await using var db = _factory.CreateContext();
        var history = await new ListCredentialsHandler(db, new FixedClockForCredentials(Now))
            .Handle(new ListCredentialsQuery(userId), CancellationToken.None);

        history.Value!.Should().HaveCount(2);
        history.Value[0].IsCurrent.Should().BeTrue();
        history.Value[1].IsCurrent.Should().BeFalse();
    }

    // ── withdrawal ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_withdrawal_closes_the_card_and_reads_back_as_Withdrawn()
    {
        // Distinct from Expired, and the distinction is load-bearing: a lapse is a renewal
        // nobody filed, a withdrawal is a decision somebody took, and only one is cleared by
        // paperwork.
        var userId = await SeedUserAsync();
        await IssueAsync(userId);

        await using (var db = _factory.CreateContext())
        {
            var closed = await new CloseCredentialHandler(
                    db, User(), new FixedClockForCredentials(Now))
                .Handle(new CloseCredentialCommand(userId, "Suspension par l'autorité"),
                    CancellationToken.None);

            closed.IsSuccess.Should().BeTrue();
        }

        (await EligibilityAsync(userId)).Status.Should().Be(DistributionEligibility.Withdrawn);
    }

    [Fact]
    public async Task Withdrawing_when_there_is_nothing_current_is_a_404()
    {
        var userId = await SeedUserAsync();

        await using var db = _factory.CreateContext();
        var closed = await new CloseCredentialHandler(db, User(), new FixedClockForCredentials(Now))
            .Handle(new CloseCredentialCommand(userId, "Départ"), CancellationToken.None);

        closed.IsFailure.Should().BeTrue();
        closed.Error.Should().Be(CredentialErrors.NotFound);
    }

    // ── eligibility ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_user_with_no_card_reads_back_as_Missing_not_null()
    {
        // The contract never returns null: "no row, so is this agent licensed?" is a question
        // every caller would have to answer, and one of them would answer it the wrong way.
        var userId = await SeedUserAsync();

        var eligibility = await EligibilityAsync(userId);

        eligibility.Should().NotBeNull();
        eligibility.Status.Should().Be(DistributionEligibility.Missing);
        eligibility.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_card_is_not_valid_before_it_was_issued_nor_after_it_expires()
    {
        var userId = await SeedUserAsync();
        await IssueAsync(
            userId,
            issuedOn: new DateOnly(2026, 6, 1),
            expiresOn: new DateOnly(2026, 12, 31));

        (await EligibilityAsync(userId, new DateOnly(2026, 5, 31))).IsValid.Should().BeFalse();
        (await EligibilityAsync(userId, new DateOnly(2026, 6, 1))).IsValid.Should().BeTrue();
        (await EligibilityAsync(userId, new DateOnly(2026, 12, 31))).IsValid.Should().BeTrue();
        (await EligibilityAsync(userId, new DateOnly(2027, 1, 1))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_lapsed_indemnity_cover_invalidates_a_card_that_is_still_in_date()
    {
        // The half an institution does not watch. CIMA Book V requires the cover, so a card
        // standing behind a lapsed one is not a licence to present anything.
        var userId = await SeedUserAsync();
        await IssueAsync(
            userId,
            issuedOn: new DateOnly(2026, 1, 1),
            expiresOn: new DateOnly(2028, 1, 1),
            indemnityExpiresOn: new DateOnly(2026, 6, 30));

        var eligibility = await EligibilityAsync(userId, new DateOnly(2026, 10, 11));

        eligibility.IsValid.Should().BeFalse();
        eligibility.HasIndemnityCover.Should().BeTrue("one is recorded; it has simply lapsed");
    }

    [Fact]
    public async Task An_ABSENT_indemnity_cover_does_not_invalidate_the_card()
    {
        // The documented asymmetry. This table is also where a card gets recorded before its
        // indemnity paperwork arrives, and refusing it for a field nobody has filled would push
        // institutions into recording no cards at all — which is strictly worse.
        var userId = await SeedUserAsync();

        await using (var db = _factory.CreateContext())
        {
            db.DistributionCredentials.Add(DistributionCredential.Issue(
                _tenant, userId, DistributionCredentialCategory.CounterPresenter,
                "v1:cipher", new string('b', 64), "DNA",
                issuedOn: new DateOnly(2026, 1, 1), expiresOn: new DateOnly(2028, 1, 1),
                createdBy: _actor, now: Now, indemnityExpiresOn: null));

            await db.SaveChangesAsync();
        }

        var eligibility = await EligibilityAsync(userId);

        eligibility.IsValid.Should().BeTrue();
        eligibility.HasIndemnityCover.Should().BeFalse();
    }

    [Fact]
    public async Task The_batched_form_answers_about_every_id_asked_about()
    {
        // The contract's promise, and what the dispatch filter relies on: a caller must be able
        // to tell "not licensed" from "I did not ask about them".
        var licensed = await SeedUserAsync();
        var unlicensed = await SeedUserAsync();
        await IssueAsync(licensed);

        await using var db = _factory.CreateContext();
        var answers = await new AdministrationModuleFacade(db, new ProductCatalogCache())
            .GetDistributionEligibilitiesAsync(
                _tenant, [licensed, unlicensed], Today, CancellationToken.None);

        answers.Should().HaveCount(2);
        answers[licensed].IsValid.Should().BeTrue();
        answers[unlicensed].Status.Should().Be(DistributionEligibility.Missing);
    }

    [Fact]
    public async Task Another_tenants_card_does_not_make_a_user_eligible()
    {
        var userId = await SeedUserAsync();
        await IssueAsync(userId);

        await using var db = _factory.CreateContext();
        var answer = await new AdministrationModuleFacade(db, new ProductCatalogCache())
            .GetDistributionEligibilityAsync(
                Guid.NewGuid(), userId, Today, CancellationToken.None);

        answer.Status.Should().Be(DistributionEligibility.Missing);
    }

    // ── the expiry report ───────────────────────────────────────────────────

    [Fact]
    public async Task The_report_surfaces_a_card_about_to_lapse_and_one_already_lapsed()
    {
        var soon = await SeedUserAsync();
        var gone = await SeedUserAsync();
        var fine = await SeedUserAsync();

        await IssueAsync(soon, cardNumber: "CP-1", expiresOn: Today.AddDays(20));
        await IssueAsync(gone, cardNumber: "CP-2",
            issuedOn: new DateOnly(2024, 1, 1), expiresOn: Today.AddDays(-5));
        await IssueAsync(fine, cardNumber: "CP-3", expiresOn: Today.AddYears(3),
            indemnityExpiresOn: Today.AddYears(3));

        await using var db = _factory.CreateContext();
        var report = await new ExpiringCredentialsHandler(db, new FixedClockForCredentials(Now))
            .Handle(new ExpiringCredentialsQuery(WithinDays: 60), CancellationToken.None);

        report.Value!.Select(r => r.UserId).Should().BeEquivalentTo([gone, soon]);
        report.Value.Single(r => r.UserId == gone).DaysUntilExpiry.Should().BeNegative();
        report.Value.Single(r => r.UserId == soon).IsValidToday.Should().BeTrue();
    }

    [Fact]
    public async Task The_report_surfaces_a_lapsed_indemnity_cover_behind_a_valid_card()
    {
        var userId = await SeedUserAsync();
        await IssueAsync(
            userId,
            expiresOn: Today.AddYears(3),
            indemnityExpiresOn: Today.AddDays(-1));

        await using var db = _factory.CreateContext();
        var report = await new ExpiringCredentialsHandler(db, new FixedClockForCredentials(Now))
            .Handle(new ExpiringCredentialsQuery(WithinDays: 0), CancellationToken.None);

        var row = report.Value!.Should().ContainSingle().Subject;
        row.IndemnityLapsed.Should().BeTrue();
        row.IsValidToday.Should().BeFalse("a lapsed cover invalidates the card");
        row.DaysUntilExpiry.Should().BePositive("the card itself is in date");
    }

    [Fact]
    public async Task The_report_ignores_superseded_rows()
    {
        // A replaced card's expiry is history. Reporting it would fill the list with cards that
        // were renewed years ago and bury the ones that matter.
        var userId = await SeedUserAsync();
        await IssueAsync(userId, cardNumber: "CP-OLD", expiresOn: Today.AddDays(3));
        await IssueAsync(userId, cardNumber: "CP-NEW",
            expiresOn: Today.AddYears(3), indemnityExpiresOn: Today.AddYears(3),
            supersedeReason: "Renouvellement");

        await using var db = _factory.CreateContext();
        var report = await new ExpiringCredentialsHandler(db, new FixedClockForCredentials(Now))
            .Handle(new ExpiringCredentialsQuery(WithinDays: 60), CancellationToken.None);

        report.Value!.Should().BeEmpty();
    }

    // ── training (CIMA Book VII, article 732) ───────────────────────────────

    [Theory]
    [InlineData(48, true)]
    [InlineData(47, false)]
    [InlineData(null, false)]
    public void The_micro_insurance_training_requirement_is_a_threshold_not_a_flag(
        int? recorded, bool expected)
    {
        // The figure is a tenant parameter rather than a constant here, so a reforming text does
        // not mean a code change.
        var credential = DistributionCredential.Issue(
            _tenant, Guid.NewGuid(), DistributionCredentialCategory.CounterPresenter,
            "v1:cipher", new string('c', 64), "DNA",
            issuedOn: new DateOnly(2026, 1, 1), expiresOn: new DateOnly(2028, 1, 1),
            createdBy: _actor, now: Now,
            trainingHours: recorded,
            trainingCompletedOn: recorded is null ? null : new DateOnly(2025, 11, 20));

        credential.MeetsTrainingRequirement(48).Should().Be(expected);
    }

    private sealed class FixedClockForCredentials(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
