namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Test doubles of the Timeline zone, on top of the module-wide ones in <c>TestSupport/</c>.
///
/// The inbox guard and the projector are REAL-semantics fakes rather than canned substitutes:
/// the whole point of the idempotency tests is to exercise the contract ("a replay must not
/// duplicate"), which a substitute returning a scripted sequence would fake away.
/// </summary>
internal sealed class RecordingTimelineEventPublisher : IEventPublisher
{
    private readonly List<IIntegrationEvent> _published = [];

    public IReadOnlyList<IIntegrationEvent> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Inbox guard with the production semantics: the first call for a given (event id, tenant)
/// wins, every later one loses. Keyed by tenant too, so the same event id replayed for two
/// tenants is processed twice — exactly like the tenant-scoped inbox table.
/// </summary>
internal sealed class FakeTimelineInboxGuard : IInboxGuard
{
    private readonly HashSet<(Guid EventId, Guid TenantId)> _seen = [];

    public int Calls { get; private set; }

    public Task<bool> TryBeginAsync(Guid eventId, Guid tenantId, string eventType, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_seen.Add((eventId, tenantId)));
    }
}

/// <summary>Captures projector calls so consumer tests can assert on what would be written.</summary>
internal sealed class RecordingTimelineProjector : IClientTimelineProjector
{
    internal sealed record Call(
        Guid TenantId, Guid ClientId, string SourceModule, string EntryType,
        DateTimeOffset OccurredAt, string Summary, string? ReferenceType, string? ReferenceId,
        string DedupKey);

    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls => _calls;

    public Task AppendAsync(
        Guid tenantId, Guid clientId, string sourceModule, string entryType, DateTimeOffset occurredAt,
        string summary, string? referenceType, string? referenceId, string dedupKey, CancellationToken ct)
    {
        _calls.Add(new Call(tenantId, clientId, sourceModule, entryType, occurredAt, summary,
            referenceType, referenceId, dedupKey));
        return Task.CompletedTask;
    }
}

/// <summary>Leads module stand-in: returns a canned history, or throws to exercise the fallback.</summary>
internal sealed class StubLeadsModule(IReadOnlyList<LeadHistoryItem>? history = null, bool throwOnHistory = false)
    : ILeadsModule
{
    public int HistoryCalls { get; private set; }

    public Task<LeadSummary?> GetLeadAsync(Guid leadId, CancellationToken ct)
        => Task.FromResult<LeadSummary?>(null);

    public Task<IReadOnlyList<LeadHistoryItem>> GetLeadHistoryAsync(
        Guid tenantId, Guid leadId, CancellationToken ct)
    {
        HistoryCalls++;

        if (throwOnHistory)
            throw new InvalidOperationException("Leads module unreachable.");

        return Task.FromResult(history ?? []);
    }
}

/// <summary>A clock frozen at a chosen instant — tenure and provisional windows need determinism.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class TimelineFixtures
{
    internal const string AgencyCode = "AG000001";

    /// <summary>A PendingKyc individual client — the only state the factory can produce.</summary>
    internal static Client PendingKyc(
        Guid tenantId,
        Guid agencyId,
        Guid createdBy,
        string clientNumber = "AG000001-2026-000001",
        string lastName = "KOUASSI",
        string firstName = "Adjoua")
        => Client.CreateIndividual(
            tenantId: tenantId,
            clientNumber: clientNumber,
            agencyId: agencyId,
            agencyCode: AgencyCode,
            advisorUserId: null,
            firstName: firstName,
            lastName: lastName,
            maidenName: null,
            gender: Gender.Female,
            encryptedDateOfBirth: null,
            dateOfBirthBlindIndex: null,
            birthPlace: null,
            nationality: "CI",
            maritalStatus: null,
            fatherName: null,
            motherName: null,
            profession: null,
            employer: null,
            encryptedDeclaredIncome: null,
            declaredIncomeCurrency: null,
            preferredLanguage: "fr",
            docType: IdentityDocumentType.NationalIdCard,
            encryptedDocNumber: null,
            docNumberBlindIndex: null,
            docIssuedOn: null,
            docExpiresOn: null,
            createdBy: createdBy);

    internal static Client Active(
        Guid tenantId, Guid agencyId, Guid createdBy, string clientNumber = "AG000001-2026-000001")
    {
        var client = PendingKyc(tenantId, agencyId, createdBy, clientNumber);
        client.ApplyKycValidated(DateTimeOffset.UtcNow, createdBy).IsSuccess.Should().BeTrue();
        return client;
    }

    internal static Client Archived(
        Guid tenantId, Guid agencyId, Guid createdBy, string clientNumber = "AG000001-2026-000002")
    {
        var client = Active(tenantId, agencyId, createdBy, clientNumber);
        client.Archive("Client deceased", createdBy).IsSuccess.Should().BeTrue();
        return client;
    }

    /// <summary>
    /// Backdates <c>CreatedAt</c>. The aggregate stamps it itself (correctly — production code
    /// must not choose it), so tenure and provisional-window tests need this reflection escape
    /// hatch rather than a test-only factory parameter that would live forever in the domain.
    /// </summary>
    internal static Client BackdatedTo(this Client client, DateTimeOffset createdAt)
    {
        typeof(Client)
            .GetProperty(nameof(Client.CreatedAt), BindingFlags.Public | BindingFlags.Instance)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(client, [createdAt]);

        return client;
    }

    internal static ClientTimelineEntry Entry(
        Guid tenantId,
        Guid clientId,
        DateTimeOffset occurredAt,
        string sourceModule = TimelineSourceModules.Customers,
        string entryType = ClientTimelineEntryTypes.ClientActivated,
        string summary = "Fait de test",
        string? dedupKey = null)
        => ClientTimelineEntry.Create(
            tenantId, clientId, sourceModule, entryType, occurredAt, summary,
            referenceType: "Client",
            referenceId: clientId.ToString("D"),
            dedupKey: dedupKey ?? $"{sourceModule}:{entryType}:{clientId:D}:{occurredAt:O}");
}
