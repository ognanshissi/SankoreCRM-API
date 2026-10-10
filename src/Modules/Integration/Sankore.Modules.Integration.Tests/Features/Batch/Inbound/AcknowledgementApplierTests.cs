namespace Sankore.Modules.Integration.Tests.Features.Batch.Inbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-25 criterion 2: each acknowledgement line moves its command to <c>Succeeded</c> with the
/// external id and its <c>integration_reference</c> row, or to <c>Rejected</c> with the reason —
/// <b>and the success half commits as one transaction</b>, which is INT-07's requirement.
/// </summary>
public sealed class AcknowledgementApplierTests
{
    private static readonly Guid CommandId = new("11111111-0000-0000-0000-00000000000c");
    private static readonly Guid CrmId = new("22222222-0000-0000-0000-00000000000d");

    [Fact]
    public async Task A_success_line_closes_the_command_and_writes_its_reference()
    {
        var db = Seed(out var connection, out var name);

        var outcome = await Apply(db, connection, Ack("OK", externalId: "CBS-42"));

        outcome.Report.Should().BeNull();
        outcome.Closed.Should().Be(CommandStatus.Succeeded);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        var command = await fresh.Commands.SingleAsync(c => c.Id == CommandId);
        command.Status.Should().Be(CommandStatus.Succeeded);
        command.ExternalResponseRef.Should().Be("CBS-42");

        var reference = await fresh.References.SingleAsync();
        reference.CrmId.Should().Be(CrmId);
        reference.ExternalId.Should().Be("CBS-42");
        reference.EntityType.Should().Be(IntegrationEntityTypes.Customer);
        reference.ConnectionId.Should().Be(InboundBatchTestContext.ConnectionA);
        reference.TenantId.Should().Be(InboundBatchTestContext.TenantA);
    }

    [Fact]
    public async Task The_status_and_the_reference_are_handed_to_ONE_save()
    {
        var watcher = new InboundBatchTestContext.SaveWatcher();
        var db = Seed(out var connection, out _, watcher);

        // Only the saves the applier itself performs — the seeding above ran on its own context.
        watcher.Saves.Clear();

        await Apply(db, connection, Ack("OK", externalId: "CBS-42"));

        watcher.Saves.Should().HaveCount(1);
        watcher.Saves[0].Should().BeEquivalentTo(
            ["IntegrationCommand:Modified", "IntegrationReference:Added"]);
    }

    [Fact]
    public async Task A_failed_save_leaves_neither_the_success_nor_the_reference()
    {
        var watcher = new InboundBatchTestContext.SaveWatcher();
        var db = Seed(out var connection, out var name, watcher);

        watcher.Saves.Clear();
        watcher.FailNextSave = true;

        var outcome = await Apply(db, connection, Ack("OK", externalId: "CBS-42"));

        // Reported, not thrown: the rest of the file must still apply.
        outcome.Closed.Should().BeNull();
        outcome.Report!.Code.Should().Be(IntegrationErrors.ConcurrencyConflict);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // Neither half landed. A success without its reference would leave a customer that exists
        // in the CBS and that SANKORE can no longer address.
        (await fresh.Commands.SingleAsync(c => c.Id == CommandId)).Status
            .Should().Be(CommandStatus.Batched);
        (await fresh.References.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_rejection_line_closes_the_command_with_its_reason()
    {
        var db = Seed(out var connection, out var name);

        var outcome = await Apply(
            db, connection,
            Ack("KO", reasonCode: "CBS-DUP-07", reasonDetail: "Client déjà enregistré"));

        outcome.Closed.Should().Be(CommandStatus.Rejected);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        var command = await fresh.Commands.SingleAsync(c => c.Id == CommandId);
        command.Status.Should().Be(CommandStatus.Rejected);
        command.LastErrorMessage.Should().Contain("CBS-DUP-07").And.Contain("déjà enregistré");

        // Functional, never Transient: the CBS answered and said no, so the dispatcher must not
        // re-send a write it has already refused on its own rules.
        command.LastErrorFamily.Should().Be(ErrorFamily.Functional);

        // A refusal creates no reference: there is nothing in the external system to address.
        (await fresh.References.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_success_with_no_external_id_is_reported_and_closes_nothing()
    {
        var db = Seed(out var connection, out _);

        var outcome = await Apply(db, connection, Ack("OK"));

        outcome.Closed.Should().BeNull();
        outcome.Report!.Code.Should().Be(InboundBatchCodes.ExternalIdMissing);

        // Left Batched on purpose: criterion 3 will alert on it rather than it being Succeeded and
        // unaddressable.
        (await db.Commands.AsNoTracking().SingleAsync(c => c.Id == CommandId)).Status
            .Should().Be(CommandStatus.Batched);
    }

    [Fact]
    public async Task A_refusal_with_no_reason_is_reported_and_closes_nothing()
    {
        var db = Seed(out var connection, out _);

        var outcome = await Apply(db, connection, Ack("KO"));

        outcome.Report!.Code.Should().Be(InboundBatchCodes.ReasonMissing);
        (await db.Commands.AsNoTracking().SingleAsync(c => c.Id == CommandId)).Status
            .Should().Be(CommandStatus.Batched);
    }

    [Theory]
    [InlineData("not-a-guid", "OK")]
    [InlineData("00000000-0000-0000-0000-000000000000", "OK")]
    public async Task A_line_whose_command_id_is_not_an_identifier_is_reported(
        string rawId, string outcomeToken)
    {
        var db = Seed(out var connection, out _);

        var record = new InboundBatchRecord(4, [rawId, outcomeToken, "X", string.Empty, string.Empty]);

        var outcome = await AcknowledgementApplier.ApplyAsync(
            db, Clock, InboundBatchTestContext.TenantA, connection, "f.csv", record,
            CancellationToken.None);

        outcome.Report!.Code.Should().Be(InboundBatchCodes.LineMalformed);
        outcome.Report.FileLine.Should().Be(4);
    }

    [Fact]
    public async Task A_line_whose_outcome_is_unknown_is_reported()
    {
        var db = Seed(out var connection, out _);

        var outcome = await Apply(db, connection, Ack("MAYBE", externalId: "X"));

        outcome.Report!.Code.Should().Be(InboundBatchCodes.LineMalformed);
    }

    [Fact]
    public async Task A_second_acknowledgement_of_the_same_command_is_reported_not_thrown()
    {
        var db = Seed(out var connection, out _);

        (await Apply(db, connection, Ack("OK", externalId: "CBS-42"))).Closed
            .Should().Be(CommandStatus.Succeeded);

        // The transition table allows Batched → Succeeded and nothing from Succeeded, so a
        // replayed file would throw if the applier did not check the status first. At-least-once
        // delivery makes a replay normal, not exceptional.
        var second = await Apply(db, connection, Ack("OK", externalId: "CBS-42"));

        second.Closed.Should().BeNull();
        second.Report!.Code.Should().Be(InboundBatchCodes.CommandNotAwaitingAck);
    }

    [Fact]
    public async Task An_external_id_that_contradicts_a_held_reference_leaves_the_command_batched()
    {
        var db = Seed(out var connection, out var name);

        db.References.Add(IntegrationReference.Create(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            IntegrationKind.Amplitude, IntegrationEntityTypes.Customer, CrmId, "CBS-ORIGINAL",
            Clock));

        await db.SaveChangesAsync();

        var outcome = await Apply(db, connection, Ack("OK", externalId: "CBS-OTHER"));

        outcome.Closed.Should().BeNull();
        outcome.Report!.Code.Should().Be(InboundBatchCodes.ReferenceConflict);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        (await fresh.Commands.SingleAsync(c => c.Id == CommandId)).Status
            .Should().Be(CommandStatus.Batched);
        (await fresh.References.SingleAsync()).ExternalId.Should().Be("CBS-ORIGINAL");
    }

    [Fact]
    public async Task An_identical_reference_is_a_replay_and_the_command_still_closes()
    {
        var db = Seed(out var connection, out var name);

        db.References.Add(IntegrationReference.Create(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            IntegrationKind.Amplitude, IntegrationEntityTypes.Customer, CrmId, "CBS-42", Clock));

        await db.SaveChangesAsync();

        (await Apply(db, connection, Ack("OK", externalId: "CBS-42"))).Closed
            .Should().Be(CommandStatus.Succeeded);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // One reference, not two: the identical row is a skip and not a second insert.
        (await fresh.References.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_debit_closes_without_a_reference_because_it_creates_no_addressable_entity()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);
        var connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        db.Connections.Add(connection);
        db.Commands.Add(InboundBatchTestContext.BatchedCommand(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            batchFileId: Guid.NewGuid(), id: CommandId, crmId: CrmId,
            type: CommandType.DebitAccount));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Apply(db, connection, Ack("OK", externalId: "TXN-9"))).Closed
            .Should().Be(CommandStatus.Succeeded);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        (await fresh.References.CountAsync()).Should().Be(0);
        (await fresh.Commands.SingleAsync()).ExternalResponseRef.Should().Be("TXN-9");
    }

    [Theory]
    [InlineData(CommandType.CreateCustomer, IntegrationEntityTypes.Customer)]
    [InlineData(CommandType.OpenAccount, IntegrationEntityTypes.Account)]
    [InlineData(CommandType.SubmitLoanApplication, IntegrationEntityTypes.Loan)]
    [InlineData(CommandType.SubscribePolicy, IntegrationEntityTypes.Policy)]
    [InlineData(CommandType.DeclareClaim, IntegrationEntityTypes.Claim)]
    public void The_five_creating_operations_map_to_the_entity_the_live_path_references(
        CommandType type, string expected)
        => AcknowledgementApplier.ReferenceEntityTypeFor(type).Should().Be(expected);

    [Theory]
    [InlineData(CommandType.UpdateCustomer)]
    [InlineData(CommandType.SetKycLevel)]
    [InlineData(CommandType.DebitAccount)]
    [InlineData(CommandType.ReverseDebit)]
    [InlineData(CommandType.CancelPolicy)]
    public void Every_other_operation_acts_on_an_entity_that_is_already_referenced(CommandType type)
        => AcknowledgementApplier.ReferenceEntityTypeFor(type).Should().BeNull();

    [Fact]
    public async Task A_command_of_another_tenant_is_never_closed()
    {
        var name = InboundBatchTestContext.NewDbName();

        await using (var seed = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantB, name))
        {
            seed.Commands.Add(InboundBatchTestContext.BatchedCommand(
                InboundBatchTestContext.TenantB, InboundBatchTestContext.ConnectionA,
                batchFileId: Guid.NewGuid(), id: CommandId, crmId: CrmId));

            await seed.SaveChangesAsync();
        }

        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);
        var connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        var outcome = await Apply(db, connection, Ack("OK", externalId: "CBS-42"));

        outcome.Report!.Code.Should().Be(InboundBatchCodes.CommandUnknown);

        await using var fresh = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantB, name);
        (await fresh.Commands.SingleAsync()).Status.Should().Be(CommandStatus.Batched);
    }

    [Fact]
    public async Task A_command_of_another_connection_of_the_same_tenant_is_never_closed()
    {
        var name = InboundBatchTestContext.NewDbName();
        var db = InboundBatchTestContext.NewDb(InboundBatchTestContext.TenantA, name);

        // The file was found in connection A's directory; the command left through B.
        db.Commands.Add(InboundBatchTestContext.BatchedCommand(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionB,
            batchFileId: Guid.NewGuid(), id: CommandId, crmId: CrmId));

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var connectionA = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        var outcome = await Apply(db, connectionA, Ack("OK", externalId: "CBS-42"));

        outcome.Report!.Code.Should().Be(InboundBatchCodes.CommandUnknown);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static TimeProvider Clock => new InboundBatchTestContext.FixedClock(
        InboundBatchTestContext.Now);

    private static IntegrationDbContext Seed(
        out IntegrationConnection connection,
        out string databaseName,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        databaseName = InboundBatchTestContext.NewDbName();

        connection = InboundBatchTestContext.Connection(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA);

        var db = InboundBatchTestContext.NewDb(
            InboundBatchTestContext.TenantA, databaseName, interceptors);

        db.Connections.Add(connection);
        db.Commands.Add(InboundBatchTestContext.BatchedCommand(
            InboundBatchTestContext.TenantA, InboundBatchTestContext.ConnectionA,
            batchFileId: Guid.NewGuid(), id: CommandId, crmId: CrmId));

        db.SaveChanges();
        db.ChangeTracker.Clear();

        return db;
    }

    private static InboundBatchRecord Ack(
        string outcome, string? externalId = null, string? reasonCode = null,
        string? reasonDetail = null)
        => new(2, [
            CommandId.ToString(),
            outcome,
            externalId ?? string.Empty,
            reasonCode ?? string.Empty,
            reasonDetail ?? string.Empty]);

    private static Task<AckLineOutcome> Apply(
        IntegrationDbContext db, IntegrationConnection connection, InboundBatchRecord record)
        => AcknowledgementApplier.ApplyAsync(
            db, Clock, InboundBatchTestContext.TenantA, connection, "SNK-ACK-000001.csv", record,
            CancellationToken.None);
}
