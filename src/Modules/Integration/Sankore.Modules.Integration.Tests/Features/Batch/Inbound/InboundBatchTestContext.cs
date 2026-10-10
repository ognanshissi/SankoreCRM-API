namespace Sankore.Modules.Integration.Tests.Features.Batch.Inbound;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The scaffolding the inbound-batch suites share: an InMemory context, a frozen clock, a
/// transport double, and a file builder that writes a well-formed envelope.
///
/// <para>
/// <b>The file builder computes the checksum for real.</b> A fixture that hardcoded a digest
/// would stop agreeing with <c>InboundBatchFileFormat</c> the moment the body changed, and every
/// test would then pass through the checksum-mismatch path while claiming to test the happy one.
/// The ONE test that needs a bad checksum corrupts the body on purpose, after the fact.
/// </para>
///
/// <para>
/// Two tenants over one physical database, like <c>TestIntegrationDbContextFactory</c>: rows are
/// seeded through one tenant's context (EF query filters apply to reads, not to inserts) and read
/// back through the other's, so an empty result is isolation and not a broken query.
/// </para>
/// </summary>
internal static class InboundBatchTestContext
{
    internal static readonly DateTimeOffset Now = new(2026, 5, 14, 7, 30, 0, TimeSpan.Zero);

    internal static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    internal static readonly Guid TenantB = new("bbbbbbbb-0000-0000-0000-000000000002");

    internal static readonly Guid ConnectionA = new("c0000000-0000-0000-0000-00000000000a");
    internal static readonly Guid ConnectionB = new("c0000000-0000-0000-0000-00000000000b");

    internal static readonly Guid Actor = new("acacacac-0000-0000-0000-000000000003");

    internal const string InboundDirectory = "/sankore/in";

    internal static string NewDbName() => $"inbound-batch-{Guid.NewGuid()}";

    /// <summary>
    /// A context over <paramref name="databaseName"/>, bound to a real
    /// <see cref="FixedTenantContext"/> so the module's global query filters are genuinely
    /// exercised — a path that forgets its tenant predicate still fails here.
    /// </summary>
    internal static IntegrationDbContext NewDb(
        Guid tenantId, string databaseName, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(databaseName);

        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);

        return new IntegrationDbContext(builder.Options, new FixedTenantContext(tenantId));
    }

    /// <summary>
    /// An ACTIVE Amplitude connection — the kind whose settings are
    /// <see cref="BatchCapableSettings"/>, which is what makes it a target of the inbound sweep.
    /// </summary>
    internal static IntegrationConnection Connection(
        Guid tenantId,
        Guid id,
        string? inboundDirectory = InboundDirectory,
        int ackTimeoutHours = 48,
        string fieldSeparator = ";",
        bool active = true)
    {
        var clock = new FixedClock(Now);

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Amplitude,
            mode: IntegrationMode.Batch,
            name: "Amplitude batch",
            settings: new AmplitudeSettings
            {
                InboundDirectory = inboundDirectory,
                OutboundDirectory = "/sankore/out",
                AckTimeoutHours = ackTimeoutHours,
                FieldSeparator = fieldSeparator,
            },
            createdBy: Actor,
            clock: clock,
            id: id);

        if (!active) return connection;

        connection.RecordHealth(IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(5), Now), clock);
        connection.Activate(Actor, clock).IsSuccess.Should().BeTrue();

        return connection;
    }

    /// <summary>
    /// A command that has left through an outbound file and is waiting for its acknowledgement —
    /// the only state criterion 2 acts on.
    /// </summary>
    internal static IntegrationCommand BatchedCommand(
        Guid tenantId,
        Guid connectionId,
        Guid batchFileId,
        Guid? id = null,
        Guid? crmId = null,
        CommandType type = CommandType.CreateCustomer)
    {
        var clock = new FixedClock(Now);
        var commandId = id ?? Guid.NewGuid();
        var crm = crmId ?? Guid.NewGuid();

        var command = IntegrationCommand.Create(
            tenantId,
            connectionId,
            type,
            type switch
            {
                CommandType.OpenAccount => IntegrationEntityTypes.Account,
                CommandType.SubscribePolicy => IntegrationEntityTypes.Policy,
                _ => IntegrationEntityTypes.Customer,
            },
            crm,
            new IdempotencyKey($"{type}:{crm}:{commandId}"),
            createdBy: Actor,
            clock: clock,
            id: commandId);

        command.BeginSending(clock);
        command.MarkBatched(batchFileId, clock);

        return command;
    }

    /// <summary>An outbound file, deposited <paramref name="sentHoursAgo"/> hours ago.</summary>
    internal static IntegrationBatchFile SentOutboundFile(
        Guid tenantId,
        Guid connectionId,
        Guid id,
        long sequenceNo = 1,
        int sentHoursAgo = 0)
    {
        var sentAt = Now.AddHours(-sentHoursAgo);

        var file = IntegrationBatchFile.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            direction: BatchDirection.Out,
            sequenceNo: sequenceNo,
            fileName: $"SNK-OUT-{sequenceNo:D6}.csv",
            checksumSha256: new string('a', 64),
            recordCount: 1,
            clock: new FixedClock(sentAt),
            id: id);

        file.MarkSent(new FixedClock(sentAt));

        return file;
    }

    /// <summary>An inbound file already recorded under its sequence.</summary>
    internal static IntegrationBatchFile RecordedInboundFile(
        Guid tenantId,
        Guid connectionId,
        long sequenceNo,
        BatchFileStatus status,
        string fileName = "SNK-ACK-000001.csv")
    {
        var clock = new FixedClock(Now);

        var file = IntegrationBatchFile.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            direction: BatchDirection.In,
            sequenceNo: sequenceNo,
            fileName: fileName,
            checksumSha256: new string('b', 64),
            recordCount: 1,
            clock: clock);

        if (status == BatchFileStatus.Processed) file.MarkProcessed(clock);
        if (status == BatchFileStatus.Failed) file.MarkFailed("seeded", clock);

        return file;
    }

    // ── Building files ──────────────────────────────────────────────────────

    /// <summary>
    /// A well-formed file: the header the format defines, with the body's real digest.
    /// </summary>
    internal static byte[] File(InboundFileKind kind, long sequenceNo, params string[] bodyLines)
    {
        var body = bodyLines.Length == 0
            ? string.Empty
            : string.Join("\n", bodyLines) + "\n";

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));

        var token = kind == InboundFileKind.Acknowledgement
            ? InboundBatchFileFormat.AcknowledgementToken
            : InboundBatchFileFormat.ExtractionToken;

        var header = $"{InboundBatchFileFormat.Magic};{InboundBatchFileFormat.FormatVersion};"
                     + $"{token};{sequenceNo};{digest}\n";

        return [.. Encoding.UTF8.GetBytes(header), .. bodyBytes];
    }

    /// <summary>
    /// The same file with its BODY corrupted after the digest was computed — so the header's
    /// checksum is honest about a body that is no longer there. That is what a truncated transfer
    /// looks like.
    /// </summary>
    internal static byte[] FileWithBrokenChecksum(
        InboundFileKind kind, long sequenceNo, params string[] bodyLines)
    {
        var good = File(kind, sequenceNo, bodyLines);
        var text = Encoding.UTF8.GetString(good);

        return Encoding.UTF8.GetBytes(text + "AN-EXTRA-TRUNCATED-LINE\n");
    }

    internal static string AckLine(Guid commandId, string outcome, string? externalId = null,
        string? reasonCode = null, string? reasonDetail = null)
        => string.Join(';', commandId.ToString(), outcome, externalId ?? string.Empty,
            reasonCode ?? string.Empty, reasonDetail ?? string.Empty);

    internal static string ExtractionLine(string externalCustomerId, params string[] ignoredColumns)
        => string.Join(';', [externalCustomerId, .. ignoredColumns]);

    // ── Doubles ─────────────────────────────────────────────────────────────

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// An in-memory inbound directory.
    ///
    /// <para>
    /// Hand-written rather than an NSubstitute mock because every test asserts on what happened to
    /// the directory — a refused file must still be there, an applied one must be gone — and a
    /// stub that only answers queries cannot say that.
    /// </para>
    /// </summary>
    internal sealed class FakeFileTransport : IIntegrationFileTransport
    {
        public Dictionary<string, byte[]> Inbound { get; } = [];

        public List<string> Archived { get; } = [];

        public List<string> Deposited { get; } = [];

        /// <summary>Set to make the listing fail, as an unreachable SFTP host does.</summary>
        public string? ListFailureCode { get; set; }

        /// <summary>File names whose read fails, as a locked or half-written file does.</summary>
        public HashSet<string> UnreadableNames { get; } = [];

        public Task<IntegrationResult> PutAsync(
            IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
        {
            Deposited.Add(fileName);
            return Task.FromResult(IntegrationResult.Ok());
        }

        public Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
            IntegrationConnection connection, CancellationToken ct)
            => Task.FromResult(ListFailureCode is { } code
                ? IntegrationResult.Transient<IReadOnlyList<string>>(code)
                : IntegrationResult.Ok<IReadOnlyList<string>>([.. Inbound.Keys.Order()]));

        public Task<IntegrationResult<byte[]>> GetInboundAsync(
            IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
        {
            if (UnreadableNames.Contains(fileName))
                return Task.FromResult(
                    IntegrationResult.Transient<byte[]>(IntegrationErrors.Unavailable));

            if (!Inbound.TryGetValue(fileName, out var content))
                return Task.FromResult(
                    IntegrationResult.Functional<byte[]>(IntegrationErrors.BatchFileNotFound));

            return Task.FromResult(content.Length > maxBytes
                ? IntegrationResult.Functional<byte[]>(IntegrationErrors.PayloadInvalid)
                : IntegrationResult.Ok(content));
        }

        public Task<IntegrationResult> ArchiveInboundAsync(
            IntegrationConnection connection, string fileName, CancellationToken ct)
        {
            Inbound.Remove(fileName);
            Archived.Add(fileName);
            return Task.FromResult(IntegrationResult.Ok());
        }
    }

    /// <summary>Records the overdue alerts the sweep raises.</summary>
    internal sealed class RecordingAlerter : IBatchAckOverdueAlerter
    {
        public List<BatchAckOverdueAlert> Alerts { get; } = [];

        public Task AlertAsync(BatchAckOverdueAlert alert, CancellationToken ct)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Watches every <c>SaveChanges</c> of a context, and can make one fail.
    ///
    /// <para>
    /// This is how criterion 2's "one transaction" is pinned. The EF InMemory provider ignores
    /// transactions, so nothing can observe a rollback directly; what IS observable, and is the
    /// actual guarantee, is that the command's modification and the reference's insert are handed
    /// to ONE save — and that when that save throws, neither of them is in the store afterwards.
    /// </para>
    ///
    /// <para>
    /// It throws <see cref="DbUpdateException"/> specifically, because that is what PostgreSQL
    /// raises on the unique index the reference row can violate, and it is what the applier's
    /// catch is written for.
    /// </para>
    /// </summary>
    internal sealed class SaveWatcher : SaveChangesInterceptor
    {
        /// <summary>One entry per save, each listing <c>EntityName:State</c>, sorted.</summary>
        public List<List<string>> Saves { get; } = [];

        public bool FailNextSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => $"{e.Entity.GetType().Name}:{e.State}")
                .Order()
                .ToList();

            Saves.Add(entries);

            if (FailNextSave)
            {
                FailNextSave = false;
                throw new DbUpdateException("Simulated unique-index violation.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
