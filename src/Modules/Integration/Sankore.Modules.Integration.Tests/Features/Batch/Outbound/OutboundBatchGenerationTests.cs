namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Criteria 2 and 3: the file is produced at the cut-off and not before, and it carries a
/// sequence unique per connection, a SHA-256 checksum over the plaintext, and a record count.
/// </summary>
public sealed class OutboundBatchGenerationTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task No_file_is_generated_before_the_cut_off()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            // Created this morning, so its cut-off (18:00 today) has not arrived.
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.BeforeCutOff)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.IsSuccess.Should().BeTrue("a quiet cycle is not an error");
        generated.Value.Generated.Should().BeFalse();

        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        // And the command is untouched — not claimed, not batched.
        var command = await db.Commands.IgnoreQueryFilters().SingleAsync();
        command.Status.Should().Be(CommandStatus.Pending);
        command.BatchFileId.Should().BeNull();
    }

    [Fact]
    public async Task At_the_cut_off_the_file_is_generated_and_its_commands_are_batched()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff, id: first));
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff.AddMinutes(30), id: second));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.IsSuccess.Should().BeTrue();
        generated.Value.Generated.Should().BeTrue();

        // Criterion 3 — a record count that matches the rows.
        generated.Value.RecordCount.Should().Be(2);

        var file = await db.BatchFiles.IgnoreQueryFilters().SingleAsync();
        file.Direction.Should().Be(BatchDirection.Out);
        file.Status.Should().Be(BatchFileStatus.Generated, "the deposit is a separate phase");
        file.RecordCount.Should().Be(2);
        file.SequenceNo.Should().Be(1, "a connection's first file is sequence 1");
        file.StorageRef.Should().NotBeNullOrWhiteSpace();

        var commands = await db.Commands.IgnoreQueryFilters().ToListAsync();

        commands.Should().OnlyContain(c => c.Status == CommandStatus.Batched);
        commands.Should().OnlyContain(c => c.BatchFileId == file.Id,
            "a command written into the file and left Pending would be sent again next cycle");
    }

    [Fact]
    public async Task A_command_created_after_the_cut_off_waits_for_the_next_cycle()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            // 18:05 — after today's 18:00 cut-off.
            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.AfterCutOff));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff.AddMinutes(10))
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.Value.Generated.Should().BeFalse();
        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_checksum_is_over_the_plaintext_and_matches_the_deposited_bytes()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        var (store, backend) = OutboundBatchTestContext.Store();
        var transport = new RecordingFileTransport();

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generator = OutboundBatchTestContext.Generator(
            db, OutboundBatchTestContext.AfterCutOff, store, transport);

        var generated = await generator.GenerateAsync(
            Tenant, connection, seedCommandId: null, CancellationToken.None);

        await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        transport.Deposits.Should().HaveCount(1);

        var sent = transport.Deposits[0].Content;
        var sentChecksum = Convert.ToHexString(SHA256.HashData(sent)).ToLowerInvariant();

        generated.Value.ChecksumSha256.Should().Be(
            sentChecksum,
            "the recorded checksum is over the plaintext the external system received");

        // And it is NOT the checksum of what sits in the store, which is ciphertext.
        var storedKey = backend.Keys.Single();
        var storedBytes = backend.Peek(storedKey)!;

        Convert.ToHexString(SHA256.HashData(storedBytes)).ToLowerInvariant()
            .Should().NotBe(generated.Value.ChecksumSha256);
    }

    [Fact]
    public async Task The_sequence_increments_per_connection_and_is_independent_between_connections()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var otherConnectionId = new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc");

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: otherConnectionId));

            await seed.SaveChangesAsync();
        }

        // Three cycles of the first connection, one of the second. Each pass is a fresh day, so a
        // new file is owed each time.
        var sequences = new List<long>();

        for (var day = 0; day < 3; day++)
        {
            var now = OutboundBatchTestContext.AfterCutOff.AddDays(day);

            await using var db = factory.CreateContext();

            db.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, now.AddHours(-2)));
            await db.SaveChangesAsync();

            var connection = await db.Connections
                .IgnoreQueryFilters().SingleAsync(c => c.Id == ConnectionId);

            var generated = await OutboundBatchTestContext.Generator(db, now)
                .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

            generated.Value.Generated.Should().BeTrue();
            sequences.Add(generated.Value.SequenceNo);
        }

        // The series is per connection and strictly increasing.
        sequences.Should().Equal([1L, 2L, 3L]);

        await using (var db = factory.CreateContext())
        {
            db.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, otherConnectionId, OutboundBatchTestContext.BeforeCutOff));
            await db.SaveChangesAsync();

            var other = await db.Connections
                .IgnoreQueryFilters().SingleAsync(c => c.Id == otherConnectionId);

            var generated = await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff)
                .GenerateAsync(Tenant, other, seedCommandId: null, CancellationToken.None);

            generated.Value.SequenceNo.Should().Be(
                1, "another connection's series starts at 1, not at the platform's maximum");
        }
    }

    [Fact]
    public async Task Concurrent_generations_of_one_connection_do_not_share_a_sequence_number()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            for (var i = 0; i < 6; i++)
                seed.Commands.Add(OutboundBatchTestContext.Queued(
                    Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff.AddMinutes(i)));

            await seed.SaveChangesAsync();
        }

        // Each run gets its own context, as two Hangfire workers would. The InMemory provider
        // enforces no unique index, so this cannot provoke the violation itself — what it pins is
        // that two runs racing over the same rows never both claim the SAME number and never both
        // claim the same command. BatchSequenceAllocationTests covers the violation's recognition,
        // and the index itself is the production guarantee (declared in the EF configuration).
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = factory.CreateContext();

            var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

            return await OutboundBatchTestContext
                .Generator(db, OutboundBatchTestContext.AfterCutOff)
                .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);
        }));

        var produced = results
            .Where(r => r.IsSuccess && r.Value.Generated)
            .Select(r => r.Value.SequenceNo)
            .ToList();

        produced.Should().OnlyHaveUniqueItems(
            "two files of one connection sharing a number would make the CBS apply a day twice "
            + "or skip one");

        await using var verify = factory.CreateContext();

        var files = await verify.BatchFiles.IgnoreQueryFilters().ToListAsync();
        files.Select(f => f.SequenceNo).Should().OnlyHaveUniqueItems();

        // No command left Pending while its data sits in a file, and none batched twice.
        var commands = await verify.Commands.IgnoreQueryFilters().ToListAsync();
        commands.Should().OnlyContain(c => c.Status == CommandStatus.Batched);

        var attachedFileIds = commands.Select(c => c.BatchFileId!.Value).Distinct();
        attachedFileIds.Should().BeSubsetOf(files.Select(f => f.Id));
    }

    [Fact]
    public async Task An_unknown_encoding_name_fails_clearly_and_produces_no_file()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId,
                fileEncoding: "ebcdic-cp-wt"));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.IsFailure.Should().BeTrue();
        generated.Family.Should().Be(
            ErrorFamily.Technical, "retrying cannot make an unknown code page appear");
        generated.Code.Should().Be(IntegrationErrors.SettingsInvalid);

        // The message has to name the setting and the value, or an operator reads it as "the
        // batch is broken" and has nowhere to go.
        generated.Detail.Should().Contain("FileEncoding").And.Contain("ebcdic-cp-wt");

        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(
            0, "a file in the wrong encoding is refused hours later by a human, not by a machine");

        (await db.Commands.IgnoreQueryFilters().SingleAsync()).Status
            .Should().Be(CommandStatus.Pending);
    }

    [Fact]
    public async Task The_configured_encoding_and_separator_are_what_is_written()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId,
                fileEncoding: "iso-8859-1", fieldSeparator: "|"));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff));

            await seed.SaveChangesAsync();
        }

        var transport = new RecordingFileTransport();

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generator = OutboundBatchTestContext.Generator(
            db, OutboundBatchTestContext.AfterCutOff, transport: transport);

        await generator.GenerateAsync(Tenant, connection, null, CancellationToken.None);
        await generator.DepositAsync(Tenant, connection, CancellationToken.None);

        var text = Encoding.GetEncoding("iso-8859-1").GetString(transport.Deposits[0].Content);

        text.Should().StartWith("command_id|command_type|", "the configured separator is used");
        text.Should().Contain("\r\n");

        // Latin-1 is a single-byte encoding: one char, one byte. Proves the bytes are not UTF-8.
        transport.Deposits[0].Content.Length.Should().Be(text.Length);
    }

    [Fact]
    public async Task A_live_claim_of_another_worker_is_not_taken_into_the_file()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var claimed = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            var command = OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff, id: claimed);

            // Claimed a minute ago by another worker: its claim is still live.
            command.BeginSending(
                new OutboundBatchTestContext.FixedClock(
                    OutboundBatchTestContext.AfterCutOff.AddMinutes(-1)));

            seed.Commands.Add(command);
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        generated.Value.Generated.Should().BeFalse(
            "a command another worker is mid-call on must not be taken over and sent twice");
    }
}
