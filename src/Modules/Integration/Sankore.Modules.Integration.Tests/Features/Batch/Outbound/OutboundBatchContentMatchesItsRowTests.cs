namespace Sankore.Modules.Integration.Tests.Features.Batch.Outbound;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The invariant the whole slice rests on: <b>the commands attached to a file are exactly the
/// commands the file's content mentions, and its record count is exactly how many.</b>
///
/// <para>
/// Both halves of breaking it are silent. A command written into the file but left unattached is
/// sent again at the next cut-off, so the CBS applies one customer twice. A command attached to a
/// file that does not mention it waits for an acknowledgement that can never arrive, and INT-25
/// eventually alerts on a write that was never made. Neither shows up as an error anywhere.
/// </para>
///
/// <para>
/// Provoked deterministically through the formatter: it is the one collaborator invoked BETWEEN
/// the read that chooses the commands and the commit that attaches them, so a callback there is
/// the race, reproducibly.
/// </para>
/// </summary>
public sealed class OutboundBatchContentMatchesItsRowTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task A_command_taken_by_another_run_mid_render_abandons_the_file()
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
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff.AddMinutes(1),
                id: second));

            await seed.SaveChangesAsync();
        }

        // While the content is being rendered, another run claims and batches the second command
        // against a file of its own.
        var formatter = new InterferingFormatter(() =>
        {
            using var otherRun = factory.CreateContext();

            var stolen = otherRun.Commands
                .AsTracking().IgnoreQueryFilters().Single(c => c.Id == second);

            var clock = new OutboundBatchTestContext.FixedClock(
                OutboundBatchTestContext.AfterCutOff);

            stolen.BeginSending(clock);
            stolen.MarkBatched(Guid.NewGuid(), clock);

            otherRun.SaveChanges();
        });

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        var generated = await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff, formatter: formatter)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        formatter.Rendered.Should().BeTrue("the interference has to have happened");

        generated.IsFailure.Should().BeTrue();
        generated.Family.Should().Be(
            ErrorFamily.Transient, "the next pass regenerates it; nothing is lost");

        // No file carrying a record count that disagrees with its rows.
        (await db.BatchFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        // And the first command is untouched, free to leave in the regenerated file.
        var untouched = await db.Commands
            .IgnoreQueryFilters().SingleAsync(c => c.Id == first);

        untouched.Status.Should().Be(CommandStatus.Pending);
        untouched.BatchFileId.Should().BeNull();
    }

    [Fact]
    public async Task The_orphaned_object_of_an_abandoned_file_is_not_left_behind()
    {
        var (store, backend) = OutboundBatchTestContext.Store();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        var only = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Connections.Add(OutboundBatchTestContext.BatchConnection(
                Tenant, OutboundBatchTestContext.BeforeCutOff, id: ConnectionId));

            seed.Commands.Add(OutboundBatchTestContext.Queued(
                Tenant, ConnectionId, OutboundBatchTestContext.BeforeCutOff, id: only));

            await seed.SaveChangesAsync();
        }

        var formatter = new InterferingFormatter(() =>
        {
            using var otherRun = factory.CreateContext();

            var stolen = otherRun.Commands
                .AsTracking().IgnoreQueryFilters().Single(c => c.Id == only);

            var clock = new OutboundBatchTestContext.FixedClock(
                OutboundBatchTestContext.AfterCutOff);

            stolen.BeginSending(clock);
            stolen.MarkBatched(Guid.NewGuid(), clock);

            otherRun.SaveChanges();
        });

        await using var db = factory.CreateContext();
        var connection = await db.Connections.IgnoreQueryFilters().SingleAsync();

        await OutboundBatchTestContext
            .Generator(db, OutboundBatchTestContext.AfterCutOff, store, formatter: formatter)
            .GenerateAsync(Tenant, connection, seedCommandId: null, CancellationToken.None);

        // The object was stored before the row was committed, so the row can never point at
        // nothing. The row never landed, so nothing else knows this reference exists — leaving it
        // would grow the volume on every occurrence of a race nobody is watching.
        backend.Keys.Should().BeEmpty();
    }

    /// <summary>
    /// A formatter that runs a callback while it renders — the one hook between the read that
    /// chooses the commands and the commit that attaches them.
    /// </summary>
    private sealed class InterferingFormatter(Action interfere) : IOutboundBatchFormatter
    {
        private readonly DelimitedOutboundBatchFormatter _inner = new();

        internal bool Rendered { get; private set; }

        public string FileExtension => _inner.FileExtension;

        public string Render(OutboundBatchContext context, IReadOnlyList<OutboundBatchRecord> records)
        {
            var text = _inner.Render(context, records);

            Rendered = true;
            interfere();

            return text;
        }
    }
}
