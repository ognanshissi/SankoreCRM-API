namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// The scaffolding the INT-24 suites share: a batch-capable connection, a real encryptor, a real
/// encrypted store over an in-memory backend, and a transport that RECORDS what it was asked.
///
/// <para>
/// The encryptor and the store are the genuine implementations, never pass-through doubles.
/// "Encrypted at rest" and "the checksum is over the plaintext" are acceptance criteria, and a
/// fake that returned its input would let a generator that forgot to encrypt pass every test in
/// this folder — the same argument <c>CommandsTestHarness</c> makes for its own encryptor.
/// </para>
/// </summary>
internal static class OutboundBatchTestContext
{
    /// <summary>A throwaway 32-byte AES-256-GCM key, base64. Test-only, never a real secret.</summary>
    internal const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    /// <summary>A second, DIFFERENT key — for the test that one key must not open the other's objects.</summary>
    internal const string OtherTestKey = "OTg3NjU0MzIxMDk4NzY1NDMyMTA5ODc2NTQzMjEwOTg=";

    /// <summary>18:00 is <c>BatchCapableSettings.CutOffTime</c>'s own default.</summary>
    internal static readonly TimeOnly CutOff = new(18, 0);

    /// <summary>Before the cut-off on 11 March 2026.</summary>
    internal static readonly DateTimeOffset BeforeCutOff = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>After it, the same day — so today's cycle is the current one.</summary>
    internal static readonly DateTimeOffset AfterCutOff = new(2026, 3, 11, 18, 5, 0, TimeSpan.Zero);

    internal static readonly Guid Actor = new("44444444-4444-4444-4444-444444444444");

    internal static TimeProvider ClockAt(DateTimeOffset now) => new FixedClock(now);

    internal static IFieldEncryptor Encryptor(string key = TestKey) => new AesGcmFieldEncryptor(
        Options.Create(new FieldProtectionOptions
        {
            SectionName = "Integration",
            FieldEncryptionKey = key,
            BlindIndexKey = key,
        }));

    /// <summary>
    /// An ACTIVE batch connection. Perfect Vision because its settings record is one of the three
    /// <c>BatchCapableSettings</c> implementations — the connection has to carry real batch
    /// coordinates, so <c>CommandsTestHarness.Connection</c>'s <c>FakeSettings</c> cannot serve
    /// here.
    /// </summary>
    internal static IntegrationConnection BatchConnection(
        Guid tenantId,
        DateTimeOffset now,
        TimeOnly? cutOff = null,
        string fileEncoding = "UTF-8",
        string fieldSeparator = ";",
        int retentionDays = 30,
        Guid? id = null,
        bool active = true,
        Guid? relayAgentId = null)
    {
        var clock = new FixedClock(now);

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.PerfectVision,
            mode: relayAgentId is null ? IntegrationMode.Batch : IntegrationMode.Relay,
            name: "Perfect Vision batch",
            settings: new PerfectVisionSettings
            {
                CutOffTime = cutOff ?? CutOff,
                FileEncoding = fileEncoding,
                FieldSeparator = fieldSeparator,
                OutboundDirectory = "/upload/sankore",
                InboundDirectory = "/download/sankore",
                SftpHost = "sftp.example.ci",
                SftpUsername = "sankore",
                RetentionDays = retentionDays,
                AckTimeoutHours = 48,
            },
            createdBy: Actor,
            clock: clock,
            relayAgentId: relayAgentId,
            id: id);

        if (!active) return connection;

        // Activation requires a passed health check — the aggregate's own rule, so a fixture that
        // wants an active connection satisfies it rather than reaching around it.
        connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(80), now), clock);

        var activated = connection.Activate(Actor, clock);
        if (activated.IsFailure)
            throw new InvalidOperationException($"Test fixture could not activate: {activated.Error}");

        return connection;
    }

    internal static IntegrationCommand Queued(
        Guid tenantId,
        Guid connectionId,
        DateTimeOffset createdAt,
        Guid? crmId = null,
        CommandType type = CommandType.CreateCustomer,
        string? payloadCiphertext = null,
        IEnumerable<string>? fieldNames = null,
        Guid? id = null)
    {
        var customer = crmId ?? Guid.NewGuid();

        return IntegrationCommand.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            commandType: type,
            entityType: IntegrationEntityTypes.Customer,
            crmId: customer,
            idempotencyKey: new IdempotencyKey($"{type}:{customer:N}:{createdAt.Ticks}"),
            createdBy: Actor,
            clock: new FixedClock(createdAt),
            payloadEncrypted: payloadCiphertext,
            payloadFieldNames: fieldNames,
            id: id);
    }

    /// <summary>The real store over an in-memory medium, so the stored bytes can be inspected.</summary>
    internal static (IBatchFileStore Store, InMemoryObjectBackend Backend) Store(
        string key = TestKey, long maxBytes = 32L * 1024 * 1024)
    {
        var backend = new InMemoryObjectBackend();

        var store = new EncryptedBatchFileStore(
            backend,
            Options.Create(new IntegrationBatchStorageOptions
            {
                EncryptionKey = key,
                MaxBytes = maxBytes,
                BasePath = "/unused-by-the-in-memory-backend",
            }),
            NullLogger<EncryptedBatchFileStore>.Instance);

        return (store, backend);
    }

    internal static OutboundBatchFileGenerator Generator(
        IntegrationDbContext db,
        DateTimeOffset now,
        IBatchFileStore? store = null,
        IIntegrationFileTransport? transport = null,
        IFieldEncryptor? encryptor = null,
        IOutboundBatchFormatter? formatter = null)
        => new(
            db,
            store ?? Store().Store,
            transport ?? new RecordingFileTransport(),
            formatter ?? new DelimitedOutboundBatchFormatter(),
            encryptor ?? Encryptor(),
            new FixedClock(now),
            NullLogger<OutboundBatchFileGenerator>.Instance);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// A transport that records every deposit instead of opening a socket, and can be told to fail.
///
/// <para>
/// Recording rather than substituting, because two of INT-24's properties are statements about
/// what WAS sent — the bytes the external system received, and the fact that a failure leaves the
/// file retryable. A substitute configured per call would let the generator deposit the wrong
/// content and still satisfy the assertion.
/// </para>
/// </summary>
internal sealed class RecordingFileTransport : IIntegrationFileTransport
{
    /// <summary>Every deposit, in order: the name and the exact bytes handed over.</summary>
    internal List<(string FileName, byte[] Content)> Deposits { get; } = [];

    /// <summary>When set, every deposit fails with it and nothing is recorded.</summary>
    internal IntegrationResult? ForcedFailure { get; set; }

    public Task<IntegrationResult> PutAsync(
        IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
    {
        if (ForcedFailure is { } failure) return Task.FromResult(failure);

        Deposits.Add((fileName, content));
        return Task.FromResult(IntegrationResult.Ok());
    }

    public Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
        IntegrationConnection connection, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Ok<IReadOnlyList<string>>([]));

    public Task<IntegrationResult<byte[]>> GetInboundAsync(
        IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Functional<byte[]>(
            IntegrationErrors.BatchFileNotFound, "The recording transport holds no inbound file."));

    public Task<IntegrationResult> ArchiveInboundAsync(
        IntegrationConnection connection, string fileName, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Ok());
}
