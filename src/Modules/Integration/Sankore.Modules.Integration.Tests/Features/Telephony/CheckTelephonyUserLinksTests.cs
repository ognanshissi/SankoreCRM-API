namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.CheckUserLinks;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The nightly check of the user links (TEL-B-05).
///
/// <para>
/// The property that matters most is the one about outages: nothing tells SANKORE that somebody
/// was removed from a provider's directory, so this job is the only thing that notices — and a
/// job that read "I could not ask" as "the account is gone" would break every link of a tenant in
/// one run and mail an administrator per agent.
/// </para>
/// </summary>
public sealed class CheckTelephonyUserLinksTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public CheckTelephonyUserLinksTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_link_whose_account_is_still_listed_is_left_alone()
    {
        await LinkAsync("fake-user-1");

        var report = await RunAsync(new FakeAdapter());

        report.Checked.Should().Be(1);
        report.Broke.Should().Be(0);

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Single().Status.Should().Be(TelUserLinkStatus.Active);
    }

    [Fact]
    public async Task An_account_no_longer_in_the_directory_breaks_the_link_and_is_published()
    {
        await LinkAsync("fake-user-gone");

        var (report, events) = await RunCountingEventsAsync(new FakeAdapter());

        report.Broke.Should().Be(1);
        events.Should().ContainSingle().Which.Should().BeOfType<TelephonyUserLinkBrokenEvent>();

        await using var db = _factory.CreateContext();
        var link = db.TelUserLinks.Single();

        link.Status.Should().Be(TelUserLinkStatus.Broken);

        // The two causes are told apart because they are different repairs: a person who left the
        // provider, versus a licence somebody can re-enable.
        link.StatusReason.Should().Contain("no longer in the provider's directory");
    }

    [Fact]
    public async Task An_account_the_provider_lists_as_disabled_says_so()
    {
        await LinkAsync("fake-user-1");

        var adapter = new FakeAdapter();
        adapter.TelephonyUsers.Clear();
        adapter.TelephonyUsers.Add(Disabled("fake-user-1"));

        var report = await RunAsync(adapter);

        report.Broke.Should().Be(1);

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Single().StatusReason.Should().Contain("disabled");
    }

    [Fact]
    public async Task A_link_already_broken_yesterday_publishes_nothing_today()
    {
        await LinkAsync("fake-user-gone");

        await RunCountingEventsAsync(new FakeAdapter());
        var (report, events) = await RunCountingEventsAsync(new FakeAdapter());

        // Published on the TRANSITION only, or an administrator is mailed every morning about an
        // account that is not coming back.
        report.Broke.Should().Be(0);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task An_account_that_comes_back_restores_the_link_without_restamping_who_vouched()
    {
        await LinkAsync("fake-user-1");

        var absent = new FakeAdapter();
        absent.TelephonyUsers.Clear();
        absent.TelephonyUsers.Add(Disabled("fake-user-1"));

        await RunAsync(absent);

        var report = await RunAsync(new FakeAdapter());

        report.Restored.Should().Be(1);

        await using var db = _factory.CreateContext();
        var link = db.TelUserLinks.Single();

        link.Status.Should().Be(TelUserLinkStatus.Active);
        link.StatusReason.Should().BeNull();

        // The human confirmation that created this pairing still stands. Stamping SYSTEM on it
        // would erase the one signature an audit asks for, to record a fact nobody vouched for.
        link.LinkedBy.Should().Be(_agent);
    }

    [Fact]
    public async Task An_unreachable_provider_breaks_nothing()
    {
        await LinkAsync("fake-user-gone");

        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout),
        };

        var (report, events) = await RunCountingEventsAsync(adapter);

        report.SkippedBecause.Should().Be(IntegrationErrors.Timeout);
        events.Should().BeEmpty();

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Single().Status.Should().Be(TelUserLinkStatus.Active);
    }

    [Fact]
    public async Task An_empty_directory_is_read_as_an_outage_too()
    {
        await LinkAsync("fake-user-1");

        var adapter = new FakeAdapter();
        adapter.TelephonyUsers.Clear();

        var report = await RunAsync(adapter);

        // A provider answering a successful empty list while its directory endpoint is
        // half-broken is indistinguishable from a tenant with no accounts — and a tenant with no
        // accounts has no links either, so skipping costs nothing.
        report.SkippedBecause.Should().Be("EMPTY_DIRECTORY");

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Single().Status.Should().Be(TelUserLinkStatus.Active);
    }

    [Fact]
    public async Task A_link_somebody_unlinked_on_purpose_is_never_revisited()
    {
        await LinkAsync("fake-user-1");

        await using (var db = _factory.CreateContext())
        {
            var link = await db.TelUserLinks.AsTracking().SingleAsync(CancellationToken.None);
            link.Disable("Compte recyclé par le fournisseur.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var report = await RunAsync(new FakeAdapter());

        // Somebody said, with a reason, that this pairing no longer holds. A sweep finding the
        // provider account still listed is not an argument against their decision.
        report.SkippedBecause.Should().Be("NO_LINKS");

        await using var after = _factory.CreateContext();
        after.TelUserLinks.Single().Status.Should().Be(TelUserLinkStatus.Disabled);
    }

    [Fact]
    public async Task A_tenant_with_no_telephony_is_skipped_quietly()
    {
        var report = await RunAsync(adapter: null);

        report.SkippedBecause.Should().Be(TelephonyErrors.Disabled);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ProviderUser Disabled(string id)
        => new(new ExternalId(id), "agent@example.test", "Agent de test", IsActive: false, Numbers: []);

    private async Task LinkAsync(string externalUserId)
    {
        await using var db = _factory.CreateContext();

        var result = await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(new LinkTelephonyUserCommand(_agent, externalUserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    private async Task<CheckTelephonyUserLinksJob.LinkCheckReport> RunAsync(FakeAdapter? adapter)
        => (await RunCountingEventsAsync(adapter)).Report;

    private async Task<(CheckTelephonyUserLinksJob.LinkCheckReport Report, List<object> Events)>
        RunCountingEventsAsync(FakeAdapter? adapter)
    {
        await using var db = _factory.CreateContext();

        var publisher = new RecordingPublisher();

        var report = await CheckTelephonyUserLinksJob.RunAsync(
            db,
            TelephonyTestHarness.Access(db, adapter),
            publisher,
            TelephonyTestHarness.Clock,
            NullLogger<CheckTelephonyUserLinksJob>.Instance,
            _tenant,
            CancellationToken.None);

        return (report, publisher.Published);
    }

    /// <summary>
    /// Counts what was published rather than writing to the outbox: what the job promises is that
    /// a transition publishes once and a confirmation publishes nothing, and the outbox's own
    /// behaviour is tested where the outbox lives.
    /// </summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
            where TEvent : IIntegrationEvent
        {
            Published.Add(@event!);

            return Task.CompletedTask;
        }
    }
}
