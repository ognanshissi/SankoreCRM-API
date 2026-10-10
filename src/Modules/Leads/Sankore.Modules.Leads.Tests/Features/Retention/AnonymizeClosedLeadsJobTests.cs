namespace Sankore.Modules.Leads.Tests.Features.Retention;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// BNF-B-19: a lead closed for longer than the tenant's retention period loses its personal data —
/// all of it, across five tables, and nothing of the funnel.
/// </summary>
public sealed class AnonymizeClosedLeadsJobTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public AnonymizeClosedLeadsJobTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private Lead Captured(string first = "Awa", string last = "Ouattara")
        => Lead.Capture(
            tenantId: _tenant, fullName: $"{first} {last}", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: Guid.NewGuid(), clock: TimeProvider.System,
            firstName: first, lastName: last, ownerId: Guid.NewGuid(),
            campaign: "RADIO-2026", nationalId: "CI0012345");

    /// <summary>A fully-populated lead, closed <paramref name="monthsAgo"/> months ago.</summary>
    private async Task<Lead> SeedClosedAsync(
        int monthsAgo = 30, LeadCloseReason reason = LeadCloseReason.Lost, bool withCustomer = false)
    {
        var lead = Captured();
        lead.SetEmail("awa@example.ci", "blind-index-awa");

        lead.EnrichPrincipalIdentity(
            null, null, "Awa K.", new DateOnly(1990, 4, 2), LeadGender.Female,
            "12 rue des Jardins", "01 BP 1234", "Abidjan", "CI", Now);

        lead.SetPrincipalPepStatus(
            PepStatus.Exposed("Députée", PepLink.Self, "CI"), Now, postMandateMonths: 12);

        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Ouattara", "Kofi", birthDate: new DateOnly(2015, 4, 2)),
            PepStatus.NotExposed, usesPrincipalAddress: true, now: Now);

        if (withCustomer)
            lead.AttachToCustomer(Guid.NewGuid(), isAutomatic: false, reason: "Known client");

        lead.Close(reason, "Pas intéressé");

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);

        var consent = LeadConsent.Create(
            tenantId: _tenant, leadId: lead.Id, type: ConsentType.HealthData,
            channel: ConsentChannel.Paper, recordedBy: Guid.NewGuid(),
            clock: TimeProvider.System, proofReference: "doc://consent/awa",
            subjectType: QualificationSubjectType.Principal, subjectId: lead.Id,
            givenBy: Guid.NewGuid());

        db.LeadConsents.Add(consent);

        var declaration = FatcaDeclaration.Record(
            tenantId: _tenant, leadId: lead.Id,
            subjectType: QualificationSubjectType.Principal, subjectId: lead.Id,
            indicia: new FatcaIndicia(UsNationalityOrResidence: true),
            documentation: FatcaDocumentation.FormW9,
            usTin: "123-45-6789", selfCertificationDocRef: "doc://self-cert/awa",
            evidenceDocRef: "doc://w9/awa", signedAt: Now, refusalReason: null,
            recordedBy: Guid.NewGuid(), now: Now);

        declaration.IsSuccess.Should().BeTrue(declaration.Error);
        db.FatcaDeclarations.Add(declaration.Value);

        var response = QualificationResponse.Create(
            tenantId: _tenant, leadId: lead.Id, templateId: Guid.NewGuid(),
            answeredBy: Guid.NewGuid(), clock: TimeProvider.System,
            answers: [(Guid.NewGuid(), "Oui, je fume")], computedScore: 40,
            questionnaireVersion: 1, subjectType: QualificationSubjectType.Principal,
            subjectId: lead.Id, isHealthData: false);

        db.QualificationResponses.Add(response);

        await db.SaveChangesAsync(CancellationToken.None);

        // Push the closure back in time. Done with a direct column write because Lead.Close
        // stamps UpdatedAt with the real clock and nothing may back-date it through the domain —
        // which is itself the right design.
        await using (var backdate = _factory.CreateContext())
        {
            var stored = await backdate.Leads.AsTracking()
                .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);

            typeof(Lead).GetProperty(nameof(Lead.UpdatedAt))!
                .SetValue(stored, Now.AddMonths(-monthsAgo));

            await backdate.SaveChangesAsync(CancellationToken.None);
        }

        return lead;
    }

    private async Task RunAsync(int retentionMonths = 24)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedRetentionClock(Now));
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton<ILeadSettings>(new FixedLeadSettings(retentionMonths));

        await using var provider = services.BuildServiceProvider();

        var job = new AnonymizeClosedLeadsJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AnonymizeClosedLeadsJob>.Instance);

        await job.RunAsync(_tenant, CancellationToken.None);
    }

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.Include(l => l.HouseholdMembers)
                             .FirstAsync(l => l.Id == leadId, CancellationToken.None);
    }

    // ── what goes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_direct_identifier_of_the_lead_is_destroyed()
    {
        var lead = await SeedClosedAsync();

        await RunAsync();

        var stored = await ReloadAsync(lead.Id);

        stored.IsAnonymized.Should().BeTrue();
        stored.AnonymizedAt.Should().Be(Now);

        stored.FirstName.Should().BeNull();
        stored.LastName.Should().BeNull();
        stored.FullName.Should().Be("[anonymisé]",
            "FullName is NOT NULL and several read sides project it directly");
        stored.Email.Should().BeNull();
        stored.NationalId.Should().BeNull();
        stored.DateOfBirth.Should().BeNull();
        stored.PrincipalAddressLine.Should().BeNull();
        stored.PrincipalCity.Should().BeNull();
        stored.Comment.Should().BeNull();
        stored.LossReason.Should().BeNull();
        stored.Location.Should().BeNull("a GPS point taken at a walk-in is the prospect's doorstep");
    }

    [Fact]
    public async Task The_blind_indexes_go_too()
    {
        // Leaving one would let somebody confirm a guessed phone number or e-mail against a row
        // whose data is supposedly gone — which is exactly a blind index working as designed.
        var lead = await SeedClosedAsync();

        await RunAsync();

        var stored = await ReloadAsync(lead.Id);
        stored.EmailBlindIndex.Should().BeNull();
        stored.PhoneBlindIndex.Should().BeNull();
        stored.LegacyEmailClear.Should().BeNull();
    }

    [Fact]
    public async Task The_PEP_classification_goes()
    {
        var lead = await SeedClosedAsync();

        await RunAsync();

        var stored = await ReloadAsync(lead.Id);
        stored.PrincipalPep.IsPep.Should().BeFalse();
        stored.PrincipalPep.Function.Should().BeNull();
        stored.PepDeclaredAt.Should().BeNull();
        stored.HasPepExposure.Should().BeFalse();
    }

    [Fact]
    public async Task The_household_members_lose_their_identities_and_keep_their_rows()
    {
        var lead = await SeedClosedAsync();

        await RunAsync();

        var member = (await ReloadAsync(lead.Id)).HouseholdMembers.Should().ContainSingle().Subject;

        member.Identity.FirstName.Should().NotBe("Kofi");
        member.Identity.BirthDate.Should().BeNull();
        member.Pep.IsPep.Should().BeFalse();
        member.Type.Should().Be(HouseholdMemberType.Child, "the row says a child existed");
    }

    [Fact]
    public async Task All_five_tables_are_cleared_in_one_pass()
    {
        var lead = await SeedClosedAsync();

        await RunAsync();

        await using var db = _factory.CreateContext();

        var response = await db.QualificationResponses
            .FirstAsync(r => r.LeadId == lead.Id, CancellationToken.None);
        response.IsPurged.Should().BeTrue();
        response.AnswersJson.Should().NotContain("fume");

        var consent = await db.LeadConsents
            .FirstAsync(c => c.LeadId == lead.Id, CancellationToken.None);
        consent.ProofReference.Should().BeNull();
        consent.GivenBy.Should().BeNull();
        consent.Type.Should().Be(ConsentType.HealthData,
            "the FACT of the consent is the record a regulator asks for");
        consent.Status.Should().Be(ConsentStatus.Active);

        var declaration = await db.FatcaDeclarations
            .FirstAsync(d => d.LeadId == lead.Id, CancellationToken.None);
        declaration.UsTin.Should().BeNull();
        declaration.EvidenceDocRef.Should().BeNull();
        declaration.Status.Should().Be(FatcaStatus.UsPerson,
            "the status is what says a certification happened");
    }

    [Fact]
    public async Task No_nominative_data_survives_anywhere()
    {
        // The DoD's integration check, as a test: serialise everything the five tables hold for
        // this lead and assert none of the names, numbers or references appear in it.
        var lead = await SeedClosedAsync();

        await RunAsync();

        await using var db = _factory.CreateContext();

        var dump = JsonSerializer.Serialize(new
        {
            Lead = await db.Leads
                .Include(l => l.HouseholdMembers)
                .Where(l => l.Id == lead.Id)
                .Select(l => new
                {
                    l.FullName, l.FirstName, l.LastName, l.Email, l.EmailBlindIndex,
                    l.PhoneNumber, l.PhoneBlindIndex, l.NationalId, l.DateOfBirth,
                    l.PrincipalUsageName, l.PrincipalAddressLine, l.PrincipalCity,
                    l.Comment, l.LossReason,
                    Pep = new { l.PrincipalPep.Function, l.PrincipalPep.CountryCode },
                    Members = l.HouseholdMembers.Select(m => new
                    {
                        m.Identity.FirstName, m.Identity.LastName, m.Identity.BirthDate,
                        m.Identity.AddressLine, m.RemovalReason,
                    }),
                })
                .ToListAsync(CancellationToken.None),

            Responses = await db.QualificationResponses
                .Where(r => r.LeadId == lead.Id)
                .Select(r => r.AnswersJson)
                .ToListAsync(CancellationToken.None),

            Consents = await db.LeadConsents
                .Where(c => c.LeadId == lead.Id)
                .Select(c => new { c.ProofReference, c.GivenBy, c.WithdrawalReason })
                .ToListAsync(CancellationToken.None),

            Fatca = await db.FatcaDeclarations
                .Where(d => d.LeadId == lead.Id)
                .Select(d => new { d.UsTin, d.SelfCertificationDocRef, d.EvidenceDocRef, d.RefusalReason })
                .ToListAsync(CancellationToken.None),
        });

        foreach (var identifier in new[]
                 {
                     "Awa", "Ouattara", "Kofi", "awa@example.ci", "CI0012345",
                     "123-45-6789", "Jardins", "Abidjan", "Députée", "fume",
                     "doc://consent/awa", "doc://w9/awa", "Pas intéressé",
                 })
        {
            dump.Should().NotContain(identifier,
                $"'{identifier}' is nominative data and must not survive anonymisation");
        }
    }

    // ── what stays ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_funnel_survives()
    {
        // Destroying it would make a retention rule a reporting outage.
        var lead = await SeedClosedAsync();

        await RunAsync();

        var stored = await ReloadAsync(lead.Id);

        stored.Id.Should().Be(lead.Id, "an audit row naming this lead must still resolve");
        stored.Source.Should().Be(LeadSource.Agency);
        stored.Campaign.Should().Be("RADIO-2026");
        stored.Status.Should().Be(LeadStatus.Lost);
        stored.InterestedProduct.Should().Be("Assurance santé");
        stored.AgencyId.Should().Be(lead.AgencyId);
        stored.CapturedAt.Should().Be(lead.CapturedAt);
    }

    // ── who is spared ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_lead_closed_more_recently_than_the_period_is_untouched()
    {
        var lead = await SeedClosedAsync(monthsAgo: 12);

        await RunAsync(retentionMonths: 24);

        (await ReloadAsync(lead.Id)).IsAnonymized.Should().BeFalse();
    }

    [Fact]
    public async Task A_LIVE_lead_is_never_anonymised_however_old()
    {
        Lead lead;
        await using (var db = _factory.CreateContext())
        {
            lead = Captured();
            db.Leads.Add(lead);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var backdate = _factory.CreateContext())
        {
            var stored = await backdate.Leads.AsTracking()
                .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);

            typeof(Lead).GetProperty(nameof(Lead.UpdatedAt))!
                .SetValue(stored, Now.AddYears(-10));

            await backdate.SaveChangesAsync(CancellationToken.None);
        }

        await RunAsync();

        (await ReloadAsync(lead.Id)).IsAnonymized.Should().BeFalse(
            "a prospect nobody has closed is still a prospect");
    }

    [Fact]
    public async Task A_converted_lead_of_an_existing_customer_loses_only_its_OWN_data()
    {
        // BNF-B-23: the client record has its own retention rule, its own job and its own
        // regulator-facing evidence. A lead's period expiring is not a reason to destroy a client
        // the institution still serves.
        var lead = await SeedClosedAsync(
            reason: LeadCloseReason.Lost, withCustomer: true);

        await RunAsync();

        var stored = await ReloadAsync(lead.Id);

        stored.IsAnonymized.Should().BeTrue();
        stored.FirstName.Should().BeNull();

        // The LINK survives: it is what says this request belonged to that client, which is
        // funnel history, and the client itself is untouched by construction — this job holds no
        // reference to M01 at all.
        stored.CustomerId.Should().Be(lead.CustomerId);
        stored.Kind.Should().Be(LeadKind.ExistingCustomer);
    }

    [Fact]
    public async Task A_second_run_does_nothing_and_does_not_re_stamp()
    {
        var lead = await SeedClosedAsync();

        await RunAsync();
        var first = (await ReloadAsync(lead.Id)).AnonymizedAt;

        await RunAsync();

        (await ReloadAsync(lead.Id)).AnonymizedAt.Should().Be(first,
            "the selection excludes anonymised leads, so a second run has nothing to find");
    }

    [Fact]
    public async Task A_retention_period_of_zero_skips_the_run_entirely()
    {
        // What a settings typo produces by accident, and the mistake is irreversible.
        var lead = await SeedClosedAsync();

        await RunAsync(retentionMonths: 0);

        (await ReloadAsync(lead.Id)).IsAnonymized.Should().BeFalse();
    }
}

/// <summary>A clock frozen at one instant, so the cutoff is deterministic.</summary>
internal sealed class FixedRetentionClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Settings with one answer. The real <c>LeadSettingsService</c> caches in a process-wide static
/// for 60 seconds, so two tests disagreeing about the retention period would see each other's.
/// </summary>
internal sealed class FixedLeadSettings(int retentionMonths) : ILeadSettings
{
    public Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct)
        => Task.FromResult(retentionMonths.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct)
        => Task.FromResult(retentionMonths);

    public Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct)
        => Task.FromResult(false);

    public Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct)
        => Task.FromResult<decimal>(retentionMonths);

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public Task<Result> SetAsync(Guid tenantId, string key, string value, Guid actor, CancellationToken ct)
        => Task.FromResult(Result.Ok());
}
