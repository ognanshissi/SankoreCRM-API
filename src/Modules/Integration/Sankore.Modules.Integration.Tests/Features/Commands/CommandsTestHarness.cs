namespace Sankore.Modules.Integration.Tests.Features.Commands;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using NSubstitute;

/// <summary>
/// The collaborators the INT-05 / INT-07 suites share: a real encryptor over a fixed key, a real
/// outbox publisher (so a test can assert on the rows a handler committed), a fixed clock, and an
/// adapter double that RECORDS what it was asked.
///
/// <para>
/// The encryptor is the genuine <see cref="AesGcmFieldEncryptor"/> and not a pass-through double,
/// deliberately: "the payload is encrypted" is an acceptance criterion, and a fake that returned
/// its input would let a handler that forgot to encrypt pass every test in this folder.
/// </para>
/// </summary>
internal static class CommandsTestHarness
{
    /// <summary>A throwaway 32-byte AES-256-GCM key, base64. Test-only, never a real secret.</summary>
    internal const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    internal static readonly DateTimeOffset Now = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    internal static TimeProvider Clock => new FixedClock(Now);

    internal static IFieldEncryptor Encryptor() => new AesGcmFieldEncryptor(
        Options.Create(new FieldProtectionOptions
        {
            SectionName = "Integration",
            FieldEncryptionKey = TestKey,
            BlindIndexKey = TestKey,
        }));

    internal static CommandPayloadProtector Protector() => new(Encryptor());

    internal static IEventPublisher Publisher(IntegrationDbContext db)
        => new OutboxEventPublisher<IntegrationDbContext>(db);

    internal static ICurrentUser User(Guid tenantId, Guid? userId = null)
        => new TestCurrentUser(userId ?? Actor, tenantId);

    /// <summary>The operator every test acts as, unless it is testing who acted.</summary>
    internal static readonly Guid Actor = new("44444444-4444-4444-4444-444444444444");

    // ── Domain fixtures ─────────────────────────────────────────────────────

    internal static IntegrationConnection Connection(
        Guid tenantId,
        IntegrationFamily family = IntegrationFamily.CoreBanking,
        IntegrationMode mode = IntegrationMode.Api,
        bool active = true,
        Guid? id = null)
    {
        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: family,
            kind: IntegrationKind.Fake,
            mode: mode,
            name: $"{family} test connection",
            settings: new FakeSettings { Family = family },
            createdBy: Actor,
            clock: new FixedClock(Now),
            id: id);

        if (!active) return connection;

        // Activation requires a passed health check — the aggregate's own rule, so a test that
        // wants an active connection has to satisfy it rather than reach around it.
        connection.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), Now), new FixedClock(Now));

        // Activate returns a Result; a fixture that silently ignored a failure would hand tests
        // an inactive connection and every "no active connection" assertion would pass by
        // accident.
        var activated = connection.Activate(Actor, new FixedClock(Now));
        if (activated.IsFailure)
            throw new InvalidOperationException($"Test fixture could not activate: {activated.Error}");

        return connection;
    }

    /// <summary>
    /// The resolver, built over a container holding one keyed adapter — the real resolution path,
    /// keyed DI included, rather than a substitute that would skip it.
    /// </summary>
    internal static IntegrationAdapterResolver Resolver(IntegrationDbContext db, ICbsAdapter? adapter)
    {
        var services = new ServiceCollection();

        if (adapter is not null)
            services.AddKeyedSingleton(adapter.Kind.ToString(), adapter);

        return new IntegrationAdapterResolver(db, services.BuildServiceProvider());
    }

    internal static CbsCustomerPayloadSource PayloadSource(
        Guid tenantId, Guid crmCustomerId, string? kycTier = "Full", bool known = true)
    {
        var customers = Substitute.For<ICustomersModule>();
        var kyc = Substitute.For<IKycModule>();

        if (known)
        {
            customers.GetClientSummaryAsync(tenantId, crmCustomerId, Arg.Any<CancellationToken>())
                .Returns(new ClientSummary(
                    Id: crmCustomerId,
                    ClientNumber: "CRM-000123",
                    ClientType: "Individual",
                    DisplayName: "AWA OUATTARA",
                    Status: "Active",
                    AgencyId: new Guid("55555555-5555-5555-5555-555555555555"),
                    AdvisorUserId: null,
                    KycStatus: "Approved",
                    RiskLevel: "Low",
                    MergedIntoId: null));

            customers.GetCustomerAsync(tenantId, crmCustomerId, Arg.Any<CancellationToken>())
                .Returns(new CustomerSummary(
                    crmCustomerId, "AWA OUATTARA", "awa@example.ci", "+2250707070707", null));
        }

        if (kycTier is not null)
            kyc.GetLimitsAsync(tenantId, crmCustomerId, Arg.Any<CancellationToken>())
                .Returns(new KycLimits(kycTier, kycTier != "Full", 2_000_000m, 500_000m, 30, 80));

        return new CbsCustomerPayloadSource(customers, kyc);
    }

    internal static ExecuteIntegrationCommandHandler ExecuteHandler(
        IntegrationDbContext db,
        ICbsAdapter? adapter,
        Guid tenantId,
        Guid crmCustomerId,
        ICommandRetryPolicy? retry = null,
        IBatchFileEnlister? batch = null,
        IEventPublisher? publisher = null,
        CbsCustomerPayloadSource? payloads = null,
        TimeProvider? clock = null)
        => new(
            db,
            Resolver(db, adapter),
            new ReferenceLookup(db),
            Protector(),
            payloads ?? PayloadSource(tenantId, crmCustomerId),
            retry ?? Retry(),
            batch ?? new UnavailableBatchFileEnlister(),
            publisher ?? Publisher(db),
            clock ?? new FixedClock(Now),
            NullLogger<ExecuteIntegrationCommandHandler>.Instance);

    /// <summary>
    /// A predictable budget: eight attempts, each retry one hour out. The real curve belongs to
    /// the dispatcher (INT-06) and has its own suite; these tests only care WHICH branch the
    /// handler takes, so pinning the curve here would couple two stories.
    /// </summary>
    internal static ICommandRetryPolicy Retry(int maxAttempts = 8)
    {
        var policy = Substitute.For<ICommandRetryPolicy>();
        policy.MaxAttempts.Returns(maxAttempts);
        policy.NextAttemptAt(Arg.Any<int>()).Returns(Now.AddHours(1));
        return policy;
    }

    internal static IntegrationCommand Queued(
        Guid tenantId,
        Guid connectionId,
        CommandType type,
        string entityType,
        Guid crmId,
        ProtectedPayload? payload = null,
        string? idempotencyKey = null)
        => IntegrationCommand.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            commandType: type,
            entityType: entityType,
            crmId: crmId,
            idempotencyKey: new IdempotencyKey(idempotencyKey ?? $"{type}:{crmId:N}"),
            createdBy: Actor,
            clock: new FixedClock(Now),
            payloadEncrypted: payload?.Ciphertext,
            payloadFieldNames: payload?.FieldNames);

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
