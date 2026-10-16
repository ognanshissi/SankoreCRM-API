namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// What every telephony suite needs: an ACTIVE telephony connection, a real adapter resolver over
/// a real keyed container, and the two protectors built on a throwaway key.
///
/// <para>
/// The encryptors are real rather than pass-through, deliberately: a journal writer that forgot to
/// encrypt a number would pass every test in this folder against a double that echoed its input,
/// and the one thing §9 asks for is that no clear number is stored or logged.
/// </para>
/// </summary>
internal static class TelephonyTestHarness
{
    /// <summary>A throwaway 32-byte AES-256-GCM key, base64. Test-only, never a real secret.</summary>
    internal const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    internal static readonly DateTimeOffset Now = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    internal static readonly Guid Actor = new("44444444-4444-4444-4444-444444444444");

    internal static TimeProvider Clock => new FixedClock(Now);

    internal static ICurrentUser User(Guid tenantId, Guid? userId = null)
        => new TestCurrentUser(userId ?? Actor, tenantId);

    private static IOptions<FieldProtectionOptions> Protection() => Options.Create(
        new FieldProtectionOptions
        {
            SectionName = "Integration",
            FieldEncryptionKey = TestKey,
            BlindIndexKey = TestKey,
        });

    internal static IFieldEncryptor Encryptor() => new AesGcmFieldEncryptor(Protection());

    internal static IBlindIndexer Indexer() => new HmacBlindIndexer(Protection());

    internal static TelephonyNumberProtector Numbers() => new(Encryptor(), Indexer());

    internal static TelephonyPayloadProtector Payloads() => new(Encryptor());

    /// <summary>
    /// An ACTIVE telephony connection. Activation goes through the aggregate's own health-check
    /// rule rather than around it: a fixture that reached past it would hand every test an
    /// inactive row and make every "telephony is off" assertion pass by accident.
    /// </summary>
    internal static IntegrationConnection ActiveTelephonyConnection(Guid tenantId, Guid? id = null)
    {
        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: IntegrationFamily.Telephony,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Telephony test connection",
            settings: new FakeSettings { Family = IntegrationFamily.Telephony },
            createdBy: Actor,
            clock: Clock,
            id: id);

        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), Clock);

        var activated = connection.Activate(Actor, Clock);

        if (activated.IsFailure)
            throw new InvalidOperationException($"Fixture could not activate: {activated.Error}");

        return connection;
    }

    /// <summary>
    /// The resolver, over a container holding one keyed adapter — the real resolution path, keyed
    /// DI included, rather than a substitute that would skip it. A null adapter is how a test
    /// exercises "the deployment shipped without one".
    /// </summary>
    internal static IntegrationAdapterResolver Resolver(IntegrationDbContext db, ICbsAdapter? adapter)
    {
        var services = new ServiceCollection();

        if (adapter is not null)
            services.AddKeyedSingleton(adapter.Kind.ToString(), adapter);

        return new IntegrationAdapterResolver(db, services.BuildServiceProvider());
    }

    internal static TelephonyAccess Access(IntegrationDbContext db, ICbsAdapter? adapter)
        => new(Resolver(db, adapter));

    /// <summary>
    /// The record-access check over substituted module contracts.
    ///
    /// <para>
    /// <paramref name="allowed"/> is explicit at every call site rather than defaulted, because a
    /// default of true would make every attachment case pass without anybody having checked a
    /// perimeter — which is precisely the defect this service was added for.
    /// </para>
    /// </summary>
    internal static TelephonyRecordAccess RecordAccess(bool allowed)
    {
        var customers = Substitute.For<Customers.PublicApi.ICustomersModule>();
        var leads = Substitute.For<Leads.PublicApi.ILeadsModule>();

        customers.CanActorAccessClientAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allowed);

        leads.CanActorWorkLeadAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(allowed);

        return new TelephonyRecordAccess(
            customers, leads, NullLogger<TelephonyRecordAccess>.Instance);
    }

    /// <summary>
    /// The agency resolver over a substituted M12 (TEL-B-19).
    ///
    /// <para>
    /// <paramref name="agencyId"/> null means M12 answers "no agency", which is also what a
    /// substitute answers by default — so a suite that does not care about the agency gets the
    /// same behaviour as before the column existed.
    /// </para>
    /// </summary>
    internal static TelephonyAgencyResolver Agencies(Guid? agencyId = null)
    {
        var administration = Substitute.For<Administration.PublicApi.IAdministrationModule>();

        administration.GetUserAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(agencyId);

        return new TelephonyAgencyResolver(
            administration, NullLogger<TelephonyAgencyResolver>.Instance);
    }

    /// <summary>
    /// The journal writer, assembled. A helper because five suites build one and the constructor
    /// has now grown twice — each growth having been five identical edits.
    /// </summary>
    internal static TelCallJournalWriter Writer(
        IntegrationDbContext db,
        Customers.PublicApi.ICustomersModule customers,
        Leads.PublicApi.ILeadsModule leads,
        Guid? agencyId = null)
        => new(
            db,
            Numbers(),
            new TelephonyCallMatcher(customers, leads, NullLogger<TelephonyCallMatcher>.Instance),
            Agencies(agencyId),
            NullLogger<TelCallJournalWriter>.Instance);

    /// <summary>
    /// The call-side perimeter check (TEL-B-19).
    ///
    /// <para>
    /// <b>No parameterless default.</b> Pass <c>null</c> for an unrestricted caller — a
    /// super-user, which <c>IAgencyScopeProvider</c> signals with a null set — or the agencies the
    /// caller supervises. Spelling it at every call site is the point: a default would make every
    /// supervisor case pass without anybody having said what perimeter they were in.
    /// </para>
    /// </summary>
    internal static TelephonyCallPerimeter Perimeter(Guid[]? accessibleAgencies)
        => new(AgencyScope(accessibleAgencies), NullLogger<TelephonyCallPerimeter>.Instance);

    /// <summary>
    /// A perimeter as <c>IAgencyScopeProvider</c> hands one back. <c>null</c> is UNRESTRICTED — a
    /// super-user — and an empty array is a caller who sees nothing; a collection expression
    /// cannot build an <c>IReadOnlySet&lt;Guid&gt;</c> directly, which is the other reason this
    /// is a helper.
    /// </summary>
    internal static Shared.Kernel.Authorization.IAgencyScopeProvider AgencyScope(
        Guid[]? accessibleAgencies)
    {
        var scope = Substitute.For<Shared.Kernel.Authorization.IAgencyScopeProvider>();

        scope.GetAccessibleAgencyIdsAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(accessibleAgencies is null
                ? (IReadOnlySet<Guid>?)null
                : accessibleAgencies.ToHashSet());

        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => accessibleAgencies is null
                || accessibleAgencies.Contains(call.ArgAt<Guid>(2)));

        return scope;
    }

    /// <summary>
    /// The screen pop over a notifier that records what it was asked to push.
    ///
    /// <para>
    /// A recorder and not <c>NullRealtimeNotifier</c>, because the property worth asserting is
    /// what the payload DOES NOT contain: TEL-B-10 says a push carries a call id and nothing
    /// else, and a no-op double could never show that.
    /// </para>
    /// </summary>
    internal static (TelephonyScreenPop Pop, RecordingNotifier Notifier) ScreenPop()
    {
        var notifier = new RecordingNotifier();

        return (new TelephonyScreenPop(notifier, NullLogger<TelephonyScreenPop>.Instance), notifier);
    }

    internal sealed class RecordingNotifier : IRealtimeNotifier
    {
        public List<(Guid TenantId, Guid UserId, string EventName, object Payload)> Pushes { get; } = [];

        /// <summary>Set to false to exercise "nobody was listening".</summary>
        public bool Delivers { get; set; } = true;

        public Task<bool> PushToUserAsync(
            Guid tenantId, Guid userId, string eventName, object payload, CancellationToken ct)
        {
            Pushes.Add((tenantId, userId, eventName, payload));

            return Task.FromResult(Delivers);
        }
    }

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestCurrentUser(Guid id, Guid tenantId) : ICurrentUser
    {
        public Guid Id => id;

        public Guid TenantId => tenantId;

        public string DisplayName => "test";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => ["Administrator"];
    }
}
