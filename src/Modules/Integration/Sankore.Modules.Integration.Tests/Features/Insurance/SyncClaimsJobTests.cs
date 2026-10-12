namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Claims.ClaimSync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-09, third and fourth criteria: a status change is announced once, and the agent is told
/// when the insurer is waiting on pièces.
///
/// <para>
/// The two cases worth reading first are
/// <see cref="An_unchanged_dossier_announces_nothing_and_raises_nothing"/> — a nightly sweep that
/// fired per dossier would mail every customer every night about nothing — and
/// <see cref="A_SECOND_request_for_documents_still_raises_a_task"/>, which is the asymmetry the
/// job is built around: the insurer asking for one more pièce while the dossier is already
/// DocumentsRequired moves no status at all, so an event-driven task would never see it.
/// </para>
/// </summary>
public sealed class SyncClaimsJobTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Loss = new(2026, 4, 10);

    private const string ExternalPolicy = FakeAdapter.SeededPolicyId;
    private const string ExternalClaim = FakeAdapter.SeededClaimId;

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly FakeAdapter _adapter = new();
    private readonly List<ClaimStatusChangedEvent> _published = [];
    private readonly List<NotifyUserRequest> _notified = [];
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();

    public SyncClaimsJobTests()
    {
        _publisher
            .When(p => p.PublishAsync(
                Arg.Any<ClaimStatusChangedEvent>(), Arg.Any<CancellationToken>()))
            .Do(call => _published.Add(call.Arg<ClaimStatusChangedEvent>()));

        _administration
            .NotifyUserAsync(Arg.Do<NotifyUserRequest>(_notified.Add), Arg.Any<CancellationToken>())
            .Returns(true);

        // The fake ships its seeded dossier with a 2024 date. Aligned with ours once, here,
        // because in reality the two agree about the day of the loss: we told the insurer. It is
        // also what the acknowledgement fallback matches on when no external id has landed yet.
        var seeded = _adapter.ClaimsByPolicy[ExternalPolicy][0];
        _adapter.ClaimsByPolicy[ExternalPolicy][0] = seeded with
        {
            OccurredOn = Loss,
            Status = ClaimStatus.Declared,
        };
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// One insurance connection, one policy the fake adapter also knows, and one dossier of ours
    /// already acknowledged by the insurer.
    /// </summary>
    private async Task<(Guid ConnectionId, Guid ClaimId)> SeedAsync(
        bool acknowledged = true,
        Guid? declaredBy = null,
        ClaimStatus ours = ClaimStatus.Declared,
        string? ourMissingDocuments = null)
    {
        await using var db = _factory.CreateContext();

        var connection = SeedConnection(db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var policy = PolicyRecord.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            crmCustomerId: Customer,
            externalPolicyId: ExternalPolicy,
            policyNumber: ExternalPolicy,
            insurerProductCode: "ASS-VIE-EMP",
            status: PolicyStatus.Issued,
            effectiveDate: new DateOnly(2026, 1, 1),
            premiumAmount: 7_500m,
            currency: "XOF",
            periodicity: PremiumPeriodicity.Annual,
            clock: Clock(Now));

        var claim = ClaimRecord.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            policyId: policy.Id,
            crmCustomerId: Customer,
            occurredOn: Loss,
            nature: "Incendie",
            declaredBy: declaredBy ?? Actor,
            clock: Clock(Now));

        if (acknowledged)
            claim.RecordInsurerReference(ExternalClaim, ExternalClaim, Clock(Now));

        if (ours != ClaimStatus.Declared || ourMissingDocuments is not null)
            claim.ApplySync(ours, null, ourMissingDocuments, Clock(Now));

        db.Policies.Add(policy);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();

        return (connection.Id, claim.Id);
    }

    /// <summary>What the insurer reports for the seeded dossier.</summary>
    private void SetInsurerClaim(
        ClaimStatus status,
        string? missingDocuments = null,
        decimal? indemnity = null)
    {
        var existing = _adapter.ClaimsByPolicy[ExternalPolicy][0];

        _adapter.ClaimsByPolicy[ExternalPolicy][0] = existing with
        {
            Status = status,
            MissingDocuments = missingDocuments,
            IndemnityAmount = indemnity,
        };
    }

    private async Task RunAsync()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(_publisher);
        services.AddSingleton(_administration);
        services.AddScoped(_ => _factory.CreateContext());
        services.AddScoped<IntegrationAdapterResolver>();
        // Keyed by the connection's kind, which is how IntegrationAdapterResolver finds it.
        services.AddKeyedSingleton<IInsuranceClaimPort>(IntegrationKind.Fake.ToString(), _adapter);
        services.AddKeyedSingleton<ICbsAdapter>(IntegrationKind.Fake.ToString(), _adapter);

        await using var provider = services.BuildServiceProvider();

        await new SyncClaimsJob(
                new ClaimSyncScopeFactory(provider), NullLogger<SyncClaimsJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);
    }

    private async Task<ClaimRecord> ClaimAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Claims.IgnoreQueryFilters().FirstAsync(c => c.Id == id);
    }

    private async Task<int> ClaimCountAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.Claims.IgnoreQueryFilters().CountAsync();
    }

    // ── the event ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_status_the_insurer_MOVED_is_announced_once()
    {
        var (_, claimId) = await SeedAsync();
        SetInsurerClaim(ClaimStatus.UnderReview);

        await RunAsync();

        (await ClaimAsync(claimId)).Status.Should().Be(ClaimStatus.UnderReview);

        var evt = _published.Should().ContainSingle().Subject;
        evt.ExternalClaimId.Should().Be(ExternalClaim);
        evt.PreviousStatus.Should().Be(nameof(ClaimStatus.Declared));
        evt.CurrentStatus.Should().Be(nameof(ClaimStatus.UnderReview));
        evt.CrmCustomerId.Should().Be(Customer);
    }

    [Fact]
    public async Task An_unchanged_dossier_announces_nothing_and_raises_nothing()
    {
        // The case the whole design rests on. Publishing per dossier per night would have M08
        // notify every customer, every night, about nothing.
        var (_, claimId) = await SeedAsync();
        SetInsurerClaim(ClaimStatus.Declared);

        await RunAsync();

        _published.Should().BeEmpty();
        _notified.Should().BeEmpty();

        // It was still read, which is what LastSyncedAt records.
        (await ClaimAsync(claimId)).LastSyncedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_settled_dossier_is_not_swept_at_all()
    {
        // Refused and Indemnified are terminal: a dossier stops costing a nightly read the
        // moment it is settled, which is what keeps this sweep proportional to the worklist.
        var (_, claimId) = await SeedAsync(ours: ClaimStatus.Indemnified);
        SetInsurerClaim(ClaimStatus.UnderReview);

        await RunAsync();

        (await ClaimAsync(claimId)).Status.Should().Be(ClaimStatus.Indemnified);
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_indemnity_amount_the_insurer_reports_is_recorded()
    {
        var (_, claimId) = await SeedAsync();
        SetInsurerClaim(ClaimStatus.Accepted, indemnity: 1_250_000m);

        await RunAsync();

        var claim = await ClaimAsync(claimId);
        claim.IndemnityAmount.Should().Be(1_250_000m);

        // And NOT paid: the insurer saying what it owes is not the money arriving. The credit's
        // reference comes from whoever reconciled it.
        claim.IndemnityCbsReference.Should().BeNull();
        claim.IndemnityPaidAt.Should().BeNull();
    }

    // ── the acknowledgement fallback ────────────────────────────────────────

    [Fact]
    public async Task A_dossier_whose_acknowledgement_never_landed_is_matched_on_its_declaration()
    {
        // Matched on the policy and the date of the loss — ux_ins_claim_declaration's own key —
        // and the reference is recorded while we are here: the sync has just learnt it. Without
        // this the dossier would never acquire an external id and would be swept for ever.
        var (_, claimId) = await SeedAsync(acknowledged: false);
        SetInsurerClaim(ClaimStatus.UnderReview);

        await RunAsync();

        var claim = await ClaimAsync(claimId);
        claim.ExternalClaimId.Should().Be(ExternalClaim);
        claim.Status.Should().Be(ClaimStatus.UnderReview);

        (await ClaimCountAsync()).Should().Be(1, "no duplicate dossier was created");
    }

    // ── the agent task ──────────────────────────────────────────────────────

    [Fact]
    public async Task Entering_DocumentsRequired_raises_a_task_for_the_declaring_agent()
    {
        var (_, claimId) = await SeedAsync();
        SetInsurerClaim(
            ClaimStatus.DocumentsRequired, missingDocuments: "Constat de police, devis réparation");

        await RunAsync();

        var task = _notified.Should().ContainSingle().Subject;
        task.UserId.Should().Be(Actor, "the agent who declared the loss can produce the pièces");
        task.TemplateKey.Should().Be(SyncClaimsJob.DocumentsRequiredTemplate);
        task.TemplateData!["missing_documents"].Should()
            .Be("Constat de police, devis réparation");

        // Deliberately no customer name and no account of the loss: a rendered body travels to a
        // mail provider and sits in its delivery log.
        task.TemplateData.Should().NotContainKey("client_name");

        _ = await ClaimAsync(claimId);
    }

    [Fact]
    public async Task A_SECOND_request_for_documents_still_raises_a_task()
    {
        // The asymmetry the job exists for: the insurer asking for one more pièce while the
        // dossier is ALREADY DocumentsRequired moves no status, so a task driven by the event
        // alone would never fire and the dossier would stall on a pièce nobody was told about.
        var (_, _) = await SeedAsync(
            ours: ClaimStatus.DocumentsRequired, ourMissingDocuments: "Constat de police");

        SetInsurerClaim(
            ClaimStatus.DocumentsRequired,
            missingDocuments: "Constat de police, devis réparation");

        await RunAsync();

        _published.Should().BeEmpty("no status moved");
        _notified.Should().ContainSingle("the list of pièces changed");
    }

    [Fact]
    public async Task The_SAME_list_of_documents_raises_nothing_further()
    {
        var (_, _) = await SeedAsync(
            ours: ClaimStatus.DocumentsRequired, ourMissingDocuments: "Constat de police");

        SetInsurerClaim(ClaimStatus.DocumentsRequired, missingDocuments: "Constat de police");

        await RunAsync();

        _notified.Should().BeEmpty();
    }

    [Fact]
    public async Task The_task_is_keyed_on_the_dossier_AND_the_list_so_a_new_request_is_a_new_mail()
    {
        var (_, _) = await SeedAsync();

        SetInsurerClaim(ClaimStatus.DocumentsRequired, missingDocuments: "Constat de police");
        await RunAsync();

        SetInsurerClaim(
            ClaimStatus.DocumentsRequired, missingDocuments: "Constat de police, devis");
        await RunAsync();

        _notified.Should().HaveCount(2);
        _notified[0].IdempotencyKey.Should().NotBe(_notified[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_dossier_declared_at_the_insurer_s_counter_raises_no_task()
    {
        // Nobody at this institution declared it, so there is nobody to address. A notification
        // to the empty user is a failed send that reads as a bug.
        var (_, _) = await SeedAsync(declaredBy: Guid.Empty);
        SetInsurerClaim(ClaimStatus.DocumentsRequired, missingDocuments: "Constat");

        await RunAsync();

        _notified.Should().BeEmpty();
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private sealed class ClaimSyncScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
