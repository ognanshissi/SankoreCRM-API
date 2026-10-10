namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Criterion 4: the deposit goes over the transport — and a transport failure leaves the file
/// <c>Generated</c>, which is what makes it retryable without a retry mechanism of its own.
/// </summary>
public sealed class OutboundBatchDepositTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task A_generated_file_is_deposited_over_the_transport_and_marked_sent()
    {
        var transport = new RecordingFileTransport();

        using var factory = await SeededAsync();

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generator = OutboundBatchTestContext.Generator(
            db, OutboundBatchTestContext.AfterCutOff, transport: transport);

        var generated = await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
        generated.Value.Generated.Should().BeTrue();

        var deposit = await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        deposit.Deposited.Should().Be(1);
        deposit.Failed.Should().Be(0);

        transport.Deposits.Should().HaveCount(1);
        transport.Deposits[0].FileName.Should().Be(generated.Value.FileName);

        var file = await db.BatchFiles.IgnoreQueryFilters().SingleAsync();
        file.Status.Should().Be(BatchFileStatus.Sent);
        file.SentAt.Should().Be(OutboundBatchTestContext.AfterCutOff);
    }

    [Fact]
    public async Task A_transport_failure_leaves_the_file_generated_and_retryable()
    {
        var transport = new RecordingFileTransport
        {
            ForcedFailure = IntegrationResult.Transient(
                IntegrationErrors.Unavailable, "the server is down"),
        };

        using var factory = await SeededAsync();

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generator = OutboundBatchTestContext.Generator(
            db, OutboundBatchTestContext.AfterCutOff, transport: transport);

        await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);

        var failed = await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        failed.Deposited.Should().Be(0);
        failed.Failed.Should().Be(1);

        var file = await db.BatchFiles.IgnoreQueryFilters().SingleAsync();

        file.Status.Should().Be(
            BatchFileStatus.Generated,
            "a Failed row would have to be revived by hand once the server came back");
        file.SentAt.Should().BeNull();
        file.FailureDetail.Should().BeNull("the transfer failing says nothing about the file");

        // The commands stay Batched: they ARE in the file, and the file is still owed.
        (await db.Commands.IgnoreQueryFilters().ToListAsync())
            .Should().OnlyContain(c => c.Status == CommandStatus.Batched);

        // Retried on the next pass, with the server back.
        transport.ForcedFailure = null;

        var retried = await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        retried.Deposited.Should().Be(1);
        transport.Deposits.Should().HaveCount(1);

        (await db.BatchFiles.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be(BatchFileStatus.Sent);
    }

    [Fact]
    public async Task Files_are_deposited_oldest_sequence_first_and_a_failure_stops_the_series()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));
            await seed.SaveChangesAsync();
        }

        // Two cycles' files, both still Generated because the server was down. ONE store across
        // the whole test: a fresh store per pass would make the deposit phase unable to read back
        // what an earlier pass wrote, which is a property of the fixture and not of the code.
        var (store, _) = OutboundBatchTestContext.Store();

        var down = new RecordingFileTransport
        {
            ForcedFailure = IntegrationResult.Transient(IntegrationErrors.Unavailable, "down"),
        };

        for (var day = 0; day < 2; day++)
        {
            var now = OutboundBatchTestContext.AfterCutOff.AddDays(day);

            await using var db = factory.CreateContext();

            db.Commands.Add(OutboundBatchTestContext.Queued(Tenant, ConnectionId, now.AddHours(-2)));
            await db.SaveChangesAsync();

            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var generator = OutboundBatchTestContext.Generator(db, now, store, down);
            await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
            await generator.DepositAsync(Tenant, connection, CancellationToken.None);
        }

        await using (var check = factory.CreateContext())
        {
            (await check.BatchFiles.IgnoreQueryFilters().ToListAsync())
                .Should().HaveCount(2).And.OnlyContain(f => f.Status == BatchFileStatus.Generated);
        }

        // Now a transport that accepts the first file and refuses the second.
        var flaky = new FirstOnlyTransport();

        await using (var db = factory.CreateContext())
        {
            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            var deposit = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff.AddDays(2), store, flaky)
                .DepositAsync(Tenant, connection, CancellationToken.None);

            deposit.Deposited.Should().Be(1);
            deposit.Failed.Should().Be(1);
        }

        flaky.Accepted.Should().HaveCount(1);

        await using (var verify = factory.CreateContext())
        {
            var files = await verify.BatchFiles
                .IgnoreQueryFilters().OrderBy(f => f.SequenceNo).ToListAsync();

            files[0].Status.Should().Be(BatchFileStatus.Sent);
            files[1].Status.Should().Be(
                BatchFileStatus.Generated,
                "a CBS that applies files in order must not receive 2 while 1 is missing");
        }
    }

    [Fact]
    public async Task A_file_whose_stored_content_no_longer_matches_its_checksum_is_not_deposited()
    {
        var (store, backend) = OutboundBatchTestContext.Store();
        var transport = new RecordingFileTransport();

        using var factory = await SeededAsync();

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generator = OutboundBatchTestContext.Generator(
            db, OutboundBatchTestContext.AfterCutOff, store, transport);

        await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);

        // Tampering: the object is replaced by something that still decrypts to nothing useful.
        // The AES-GCM tag catches it first, which is the stronger failure — either way the file
        // must not be deposited, because the row's checksum is the only evidence of what was sent.
        backend.Overwrite(backend.Keys.Single(), [1, 2, 3, 4, 5, 6, 7, 8]);

        var deposit = await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        deposit.Deposited.Should().Be(0);
        transport.Deposits.Should().BeEmpty();

        (await db.BatchFiles.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be(BatchFileStatus.Failed, "the content cannot be vouched for any more");
    }

    private static async Task<TestIntegrationDbContextFactory> SeededAsync()
    {
        var factory = new TestIntegrationDbContextFactory(Tenant);

        await using var seed = factory.CreateContext();

        seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

        seed.Commands.Add(OutboundBatchTestContext.Queued(
            Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

        await seed.SaveChangesAsync();

        return factory;
    }

    /// <summary>Accepts the first deposit of its life and refuses every one after it.</summary>
    private sealed class FirstOnlyTransport : IIntegrationFileTransport
    {
        internal List<string> Accepted { get; } = [];

        public Task<IntegrationResult> PutAsync(
            IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
        {
            if (Accepted.Count > 0)
                return Task.FromResult(IntegrationResult.Transient(
                    IntegrationErrors.Unavailable, "the server dropped the second transfer"));

            Accepted.Add(fileName);
            return Task.FromResult(IntegrationResult.Ok());
        }

        public Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
            IntegrationConnection connection, CancellationToken ct)
            => Task.FromResult(IntegrationResult.Ok<IReadOnlyList<string>>([]));

        public Task<IntegrationResult<byte[]>> GetInboundAsync(
            IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
            => Task.FromResult(IntegrationResult.Functional<byte[]>(
                IntegrationErrors.BatchFileNotFound, "none"));

        public Task<IntegrationResult> ArchiveInboundAsync(
            IntegrationConnection connection, string fileName, CancellationToken ct)
            => Task.FromResult(IntegrationResult.Ok());
    }
}
