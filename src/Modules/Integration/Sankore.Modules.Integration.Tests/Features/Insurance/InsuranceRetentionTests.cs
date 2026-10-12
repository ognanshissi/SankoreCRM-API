namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Retention;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-12: dating the insurance evidence, then destroying what has outlived its retention.
///
/// <para>
/// This is the one job in the module whose mistake cannot be corrected by running it again, so the
/// cases are weighted towards the refusals. The three that matter most are
/// <see cref="An_undated_row_is_never_purged"/> — null means "nobody has decided", not
/// "sweepable" — <see cref="A_retention_date_is_never_brought_FORWARD"/>, without which a purge
/// could be made to happen early by editing a configuration file, and
/// <see cref="A_medical_questionnaire_is_DELETED_and_a_consent_proof_is_blanked"/>, which is the
/// judgement the two aggregates differ on.
/// </para>
/// </summary>
public sealed class InsuranceRetentionTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 7, 2, 30, 0, TimeSpan.Zero);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// One consent proof and one medical questionnaire, captured whenever the case needs.
    /// </summary>
    private async Task<(Guid ProofId, Guid QuestionnaireId)> SeedAsync(
        DateTimeOffset? capturedAt = null,
        DateTimeOffset? retainUntil = null)
    {
        await using var db = _factory.CreateContext();

        var subscriptionId = Guid.NewGuid();
        var at = capturedAt ?? Now;

        var proof = ConsentProof.Create(
            tenantId: Tenant,
            subscriptionId: subscriptionId,
            crmCustomerId: Customer,
            channel: ConsentChannel.OneTimeCode,
            consentTextVersion: "CG-2026-03",
            consentedAt: at,
            capturedBy: Actor,
            clock: Clock(at),
            evidenceEncrypted: "v1:nonce:tag:ciphertext",
            evidenceSha256: new string('a', 64),
            contentType: "text/plain",
            retainUntil: retainUntil);

        var questionnaire = MedicalQuestionnaire.Create(
            tenantId: Tenant,
            subscriptionId: subscriptionId,
            crmCustomerId: Customer,
            questionnaireCode: "MED-VIE-01",
            answersEncrypted: "v1:nonce:tag:answers",
            capturedBy: Actor,
            clock: Clock(at),
            requiresMedicalUnderwriting: true,
            retainUntil: retainUntil);

        db.ConsentProofs.Add(proof);
        db.MedicalQuestionnaires.Add(questionnaire);
        await db.SaveChangesAsync();

        return (proof.Id, questionnaire.Id);
    }

    private async Task RunAsync(
        bool purgeEnabled = true, int years = 10, int batchSize = 500)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new InsuranceRetentionOptions
        {
            EvidenceYears = years,
            BatchSize = batchSize,
            PurgeEnabled = purgeEnabled,
        }));
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        await new PurgeInsuranceEvidenceJob(
                new RetentionScopeFactory(provider),
                NullLogger<PurgeInsuranceEvidenceJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);
    }

    private async Task<ConsentProof?> ProofAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.ConsentProofs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id);
    }

    private async Task<MedicalQuestionnaire?> QuestionnaireAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.MedicalQuestionnaires
            .IgnoreQueryFilters().FirstOrDefaultAsync(q => q.Id == id);
    }

    // ── pass one: the dates ─────────────────────────────────────────────────

    [Fact]
    public async Task An_undated_row_is_dated_from_when_it_was_CAPTURED()
    {
        // Not from tonight. Counting from the run would restart a decade for evidence collected
        // years ago, which is a retention rule that never expires.
        var captured = new DateTimeOffset(2020, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var (proofId, questionnaireId) = await SeedAsync(capturedAt: captured);

        await RunAsync(purgeEnabled: false, years: 10);

        (await ProofAsync(proofId))!.RetainUntil.Should().Be(captured.AddYears(10));
        (await QuestionnaireAsync(questionnaireId))!.RetainUntil.Should().Be(captured.AddYears(10));
    }

    [Fact]
    public async Task Dating_runs_even_when_the_purge_is_OFF()
    {
        // Recording when a row becomes purgeable destroys nothing, and a deployment that never
        // enables the purge still owes a controller honest retention dates.
        var (proofId, _) = await SeedAsync(capturedAt: new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await RunAsync(purgeEnabled: false);

        var proof = (await ProofAsync(proofId))!;
        proof.RetainUntil.Should().NotBeNull();

        // Long past, and still untouched: the gate is on the destruction, not on the dating.
        proof.RetainUntil.Should().BeBefore(Now);
        proof.IsPurged.Should().BeFalse();
        proof.EvidenceEncrypted.Should().NotBeNull();
    }

    [Fact]
    public async Task A_retention_date_is_never_brought_FORWARD()
    {
        // The guard that stops a purge being made to happen early by editing a configuration
        // file: the setting decides the period for rows that have none, not for rows that have one.
        var captured = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var far = captured.AddYears(30);

        var (proofId, _) = await SeedAsync(capturedAt: captured, retainUntil: far);

        await RunAsync(purgeEnabled: true, years: 1);

        var proof = (await ProofAsync(proofId))!;
        proof.RetainUntil.Should().Be(far);
        proof.IsPurged.Should().BeFalse();
    }

    [Fact]
    public void A_date_may_be_EXTENDED_though()
    {
        // A longer national period, or a dispute that freezes a file.
        var proof = ConsentProof.Create(
            tenantId: Tenant, subscriptionId: Guid.NewGuid(), crmCustomerId: Customer,
            channel: ConsentChannel.OneTimeCode, consentTextVersion: "CG-2026-03",
            consentedAt: Now, capturedBy: Actor, clock: Clock(Now),
            evidenceEncrypted: "v1:x", retainUntil: Now.AddYears(10));

        proof.DecideRetention(Now.AddYears(5)).Should().BeFalse("that would be shortening it");
        proof.RetainUntil.Should().Be(Now.AddYears(10));

        proof.DecideRetention(Now.AddYears(15)).Should().BeTrue();
        proof.RetainUntil.Should().Be(Now.AddYears(15));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(500, 10)]
    [InlineData(15, 15)]
    public void An_absurd_configured_period_falls_back_to_the_floor(int configured, int expected)
    {
        // Zero years would purge evidence the day it is collected, and a typo in a configuration
        // file is exactly how that would arrive. Clamped rather than refused at boot: an API that
        // will not start is a worse outcome than one running on the legal floor.
        new InsuranceRetentionOptions { EvidenceYears = configured }
            .ResolvedYears.Should().Be(expected);
    }

    // ── pass two: the destruction ───────────────────────────────────────────

    [Fact]
    public async Task A_medical_questionnaire_is_DELETED_and_a_consent_proof_is_blanked()
    {
        // The judgement the two aggregates differ on. The proof's skeleton — consent taken, under
        // which notice version, when, by whom — carries no personal content and survives. The
        // questionnaire has no such skeleton: RequiresMedicalUnderwriting IS health data about a
        // named person, so keeping it would preserve the most sensitive bit under the name of a
        // purge.
        var captured = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (proofId, questionnaireId) = await SeedAsync(
            capturedAt: captured, retainUntil: captured.AddYears(10));

        await RunAsync();

        var proof = (await ProofAsync(proofId))!;
        proof.Should().NotBeNull();
        proof.IsPurged.Should().BeTrue();
        proof.PurgedAt.Should().Be(Now);

        // The artefact is gone.
        proof.EvidenceEncrypted.Should().BeNull();
        proof.EvidenceStorageRef.Should().BeNull();
        proof.EvidenceSha256.Should().BeNull();
        proof.ContentType.Should().BeNull();

        // The fact remains.
        proof.ConsentTextVersion.Should().Be("CG-2026-03");
        proof.ConsentedAt.Should().Be(captured);
        proof.CapturedBy.Should().Be(Actor);

        (await QuestionnaireAsync(questionnaireId)).Should().BeNull();
    }

    [Fact]
    public async Task An_undated_row_is_never_purged()
    {
        // Null means "no retention period has been decided" and emphatically not "sweepable now".
        // The job's first pass dates it; a run whose dating pass somehow left a null must not
        // destroy it.
        var (proofId, questionnaireId) = await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            var proof = await db.ConsentProofs.IgnoreQueryFilters().AsTracking()
                .FirstAsync(c => c.Id == proofId);

            // The aggregate itself refuses, which is what makes the rule independent of the job.
            var act = () => proof.PurgeEvidence(Clock(Now));

            act.Should().Throw<DomainException>()
                .WithMessage("*no decided retention date*");
        }

        (await ProofAsync(proofId))!.IsPurged.Should().BeFalse();
        (await QuestionnaireAsync(questionnaireId)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_row_whose_date_has_NOT_passed_survives()
    {
        var (proofId, questionnaireId) = await SeedAsync(retainUntil: Now.AddYears(10));

        await RunAsync();

        (await ProofAsync(proofId))!.IsPurged.Should().BeFalse();
        (await QuestionnaireAsync(questionnaireId)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_aggregate_refuses_a_purge_before_the_date_even_if_a_caller_asks()
    {
        await using var db = _factory.CreateContext();

        var proof = ConsentProof.Create(
            tenantId: Tenant, subscriptionId: Guid.NewGuid(), crmCustomerId: Customer,
            channel: ConsentChannel.OneTimeCode, consentTextVersion: "CG-2026-03",
            consentedAt: Now, capturedBy: Actor, clock: Clock(Now),
            evidenceEncrypted: "v1:x", retainUntil: Now.AddDays(1));

        var act = () => proof.PurgeEvidence(Clock(Now));

        act.Should().Throw<DomainException>().WithMessage("*retained until*");
    }

    [Fact]
    public async Task Purging_twice_is_a_no_op_rather_than_a_second_purge()
    {
        var captured = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (proofId, _) = await SeedAsync(
            capturedAt: captured, retainUntil: captured.AddYears(10));

        await RunAsync();
        var first = (await ProofAsync(proofId))!.PurgedAt;

        await RunAsync();

        (await ProofAsync(proofId))!.PurgedAt.Should().Be(first);
    }

    [Fact]
    public async Task The_batch_ceiling_bounds_one_run_and_the_next_continues()
    {
        var captured = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 3; i++)
            await SeedAsync(capturedAt: captured.AddDays(i), retainUntil: captured.AddYears(10));

        await RunAsync(batchSize: 2);

        await using (var db = _factory.CreateContext())
        {
            (await db.ConsentProofs.IgnoreQueryFilters().CountAsync(c => c.PurgedAt != null))
                .Should().Be(2);
        }

        // The selection is "whatever is still expired", so the next run finishes the job rather
        // than needing a cursor.
        await RunAsync(batchSize: 2);

        await using (var db = _factory.CreateContext())
        {
            (await db.ConsentProofs.IgnoreQueryFilters().CountAsync(c => c.PurgedAt == null))
                .Should().Be(0);
        }
    }

    [Fact]
    public async Task Another_tenant_s_evidence_is_never_touched()
    {
        // The job runs under IgnoreQueryFilters with an explicit tenant predicate, because a
        // Hangfire job has no ambient tenant to trust. For a job that DESTROYS data, that rule is
        // not a convention.
        var captured = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var other = Guid.NewGuid();

        await using (var db = _factory.CreateContext())
        {
            db.ConsentProofs.Add(ConsentProof.Create(
                tenantId: other, subscriptionId: Guid.NewGuid(), crmCustomerId: Guid.NewGuid(),
                channel: ConsentChannel.OneTimeCode, consentTextVersion: "CG-2026-03",
                consentedAt: captured, capturedBy: Actor, clock: Clock(captured),
                evidenceEncrypted: "v1:other-tenant", retainUntil: captured.AddYears(10)));

            await db.SaveChangesAsync();
        }

        await RunAsync();

        await using (var db = _factory.CreateContext())
        {
            var theirs = await db.ConsentProofs
                .IgnoreQueryFilters().FirstAsync(c => c.TenantId == other);

            theirs.IsPurged.Should().BeFalse();
            theirs.EvidenceEncrypted.Should().Be("v1:other-tenant");
        }
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private sealed class RetentionScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
