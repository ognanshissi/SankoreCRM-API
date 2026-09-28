namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class ClientTimelineProjectorTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ClientTimelineProjector BuildProjector(CustomersDbContext db)
        => new(db, NullLogger<ClientTimelineProjector>.Instance);

    [Fact]
    public async Task Appends_an_entry_with_its_dedup_key()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientActivated,
            DateTimeOffset.UtcNow, "Client actif — KYC validé", "Client", ClientId.ToString("D"),
            "Customers:CLIENT_ACTIVATED:e1", CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientTimelineEntries.SingleAsync();

        stored.TenantId.Should().Be(TenantId);
        stored.ClientId.Should().Be(ClientId);
        stored.SourceModule.Should().Be("Customers");
        stored.EntryType.Should().Be("CLIENT_ACTIVATED");
        stored.DedupKey.Should().Be("Customers:CLIENT_ACTIVATED:e1");
    }

    [Fact]
    public async Task A_replayed_fact_with_the_same_dedup_key_never_creates_a_second_entry()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        var occurredAt = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await projector.AppendAsync(
                TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
                occurredAt, "Client enregistré", "Client", ClientId.ToString("D"),
                "Customers:CLIENT_CREATED:same-event", CancellationToken.None);
        }

        await using var assertions = _factory.CreateContext();
        var entries = await assertions.ClientTimelineEntries.ToListAsync();

        entries.Should().HaveCount(1, "the dedup key is the second guard behind the inbox");
    }

    [Fact]
    public async Task Two_facts_of_the_same_type_with_different_dedup_keys_both_land()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientSuspended,
            DateTimeOffset.UtcNow.AddDays(-2), "Client suspendu — motif : revue", "Client",
            ClientId.ToString("D"), "Customers:CLIENT_SUSPENDED:a", CancellationToken.None);

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientSuspended,
            DateTimeOffset.UtcNow, "Client suspendu — motif : fraude", "Client",
            ClientId.ToString("D"), "Customers:CLIENT_SUSPENDED:b", CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.ClientTimelineEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Never_stores_an_identity_document_number_a_phone_an_email_or_a_date_of_birth()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        // Everything a careless producer could paste into a summary.
        const string Poisoned =
            "Pièce CI0123456789 — tél +225 07 11 22 33 — mail adjoua.kouassi@example.com — né le 1987-03-12";

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
            DateTimeOffset.UtcNow, Poisoned, null, null, "Customers:CLIENT_CREATED:poison",
            CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var summary = (await assertions.ClientTimelineEntries.SingleAsync()).Summary;

        summary.Should().NotContain("CI0123456789");
        summary.Should().NotContain("0123456789");
        summary.Should().NotContain("@example.com");
        summary.Should().NotContain("1987-03-12");
        summary.Should().Contain(TimelineSummaryGuard.Redacted);
        // The non-sensitive scaffolding survives, so the entry stays readable.
        summary.Should().Contain("Pièce");
    }

    [Fact]
    public async Task Keeps_a_client_number_intact_while_redacting_a_phone_number()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
            DateTimeOffset.UtcNow, "Dossier AG1-2026-000123 ouvert, tél 0711223344", null, null,
            "Customers:CLIENT_CREATED:mixed", CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var summary = (await assertions.ClientTimelineEntries.SingleAsync()).Summary;

        summary.Should().Contain("AG1-2026-000123", "a client number is not a sensitive value");
        summary.Should().NotContain("0711223344");
    }

    [Fact]
    public async Task Keeps_an_all_digit_identifier_reference_intact()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        const string GroupId = "11111111-2222-3333-4444-000000000002";

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers,
            ClientTimelineEntryTypes.GroupMembershipChanged, DateTimeOffset.UtcNow,
            $"Adhésion au groupe {GroupId} — rôle President", "ClientGroup", GroupId,
            "Customers:GROUP_MEMBERSHIP_CHANGED:guid", CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var summary = (await assertions.ClientTimelineEntries.SingleAsync()).Summary;

        summary.Should().Contain(GroupId, "an identifier is a navigation reference, not a secret");
    }

    [Fact]
    public async Task Truncates_a_summary_longer_than_the_column()
    {
        await using var db = _factory.CreateContext();
        var projector = BuildProjector(db);

        await projector.AppendAsync(
            TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
            DateTimeOffset.UtcNow, new string('x', 900), null, null, "Customers:CLIENT_CREATED:long",
            CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        (await assertions.ClientTimelineEntries.SingleAsync()).Summary.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task The_same_dedup_key_in_two_tenants_produces_one_entry_per_tenant()
    {
        await using (var db = _factory.CreateContext())
        {
            var projector = BuildProjector(db);

            await projector.AppendAsync(
                TenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
                DateTimeOffset.UtcNow, "Client enregistré", null, null, "shared-key", CancellationToken.None);

            await projector.AppendAsync(
                OtherTenantId, ClientId, TimelineSourceModules.Customers, ClientTimelineEntryTypes.ClientCreated,
                DateTimeOffset.UtcNow, "Client enregistré", null, null, "shared-key", CancellationToken.None);
        }

        await using var assertions = _factory.CreateContext();

        // The ambient filter is tenant A: only its own entry is visible.
        (await assertions.ClientTimelineEntries.CountAsync()).Should().Be(1);

        var all = await assertions.ClientTimelineEntries.IgnoreQueryFilters().ToListAsync();
        all.Should().HaveCount(2);
        all.Select(e => e.TenantId).Should().BeEquivalentTo(new[] { TenantId, OtherTenantId });
    }
}
