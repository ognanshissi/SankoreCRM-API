namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — <b>the inbound half of the batch socle for ONE tenant (INT-25)</b>. Enqueued by
/// <see cref="InboundBatchPollOrchestratorJob"/>; its only argument is an opaque tenant
/// identifier.
///
/// <para>
/// It carries all four criteria, in this order, per file-based connection of the tenant:
/// </para>
/// <list type="number">
///   <item>read the inbound directory, verify the checksum and the sequence, ignore a file
///         already processed;</item>
///   <item>close each acknowledged command — <c>Succeeded</c> with its external id and its
///         <c>integration_reference</c> row, or <c>Rejected</c> with its reason;</item>
///   <item>alert on a <c>Batched</c> command whose file was never acknowledged within the
///         connection's delay;</item>
///   <item>hand every customer an extraction names to INT-21's projector.</item>
/// </list>
///
/// <para>
/// <b>Criterion 3 runs in this same job rather than in its own.</b> An acknowledgement that never
/// came is noticed by exactly the thing that was waiting for it, and the two halves read the same
/// rows of the same connections — a second orchestrator and a second schedule would double the
/// moving parts to answer the same question one minute later.
/// </para>
///
/// <para>
/// <b>On the named queue <c>integration-batch</c></b>. A sweep that opens an SFTP connection and
/// reads a multi-megabyte file holds its worker for a long time by the standards of this
/// platform; it must not share a pool with the dispatcher, whose calls are rationed by a CBS
/// licence, nor with the interactive imports on <c>default</c>.
/// <b>The host must declare that queue</b> — <c>DispatchServiceRegistration</c> carries the
/// <c>BackgroundJobServerOptions</c>, and without it this job is enqueued to a queue no worker
/// reads and never runs at all, with nothing in the logs.
/// </para>
///
/// <para>
/// <b>A file is never applied in part.</b> The checksum and the sequence are verified before a
/// single record is read, because a half-applied acknowledgement file closes the wrong commands —
/// and closing a command wrongly means telling a customer their account exists when it does not.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class PollInboundBatchFilesJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>The batch queue of the module, declared by the host's server options.</summary>
    public const string QueueName = "integration-batch";

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created. ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — same rule, same reason, as every job in this
        // repository. The actor is SYSTEM: nobody decided that a file had arrived, a schedule did.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<PollInboundBatchFilesJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<IIntegrationFileTransport>(),
            sp.GetRequiredService<ICbsSnapshotProjector>(),
            // GetRequiredService on purpose: see IBatchAckOverdueAlerter. A missing registration
            // fails this sweep loudly instead of skipping criterion 3 in silence.
            sp.GetRequiredService<IBatchAckOverdueAlerter>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Inbound batch sweep for tenant {TenantId}: {Listed} file(s) listed, {Processed} "
            + "processed, {Skipped} already recorded, {Failed} refused, {Unreadable} unreadable; "
            + "{Succeeded} command(s) succeeded, {Rejected} rejected, {Projected} customer(s) "
            + "projected, {Alerts} overdue alert(s), {Reported} line(s) reported.",
            tenantId, report.Listed, report.Processed, report.Skipped, report.Failed,
            report.Unreadable, report.CommandsSucceeded, report.CommandsRejected,
            report.CustomersProjected, report.AckOverdueAlerts, report.Lines.Count);
    }

    /// <summary>
    /// The sweep itself, with its collaborators passed in rather than resolved — so a test pins
    /// the rules without a DI container and a Hangfire storage. The scope handling is the only
    /// thing <see cref="ExecuteAsync"/> adds, and it is identical in every job of this repository.
    /// </summary>
    internal static async Task<InboundBatchPollReport> RunAsync(
        IntegrationDbContext db,
        IIntegrationFileTransport transport,
        ICbsSnapshotProjector projector,
        IBatchAckOverdueAlerter alerter,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);

        var report = new InboundBatchPollReport();

        foreach (var target in await InboundBatchTargets.ListAsync(db, tenantId, ct))
        {
            if (target.CanPoll)
                await PollConnectionAsync(
                    db, transport, projector, clock, logger, tenantId, target.Connection, report, ct);

            // Criterion 3, after the poll of the same connection: a file acknowledged one minute
            // ago must not be alerted on as overdue in the same run.
            report.AckOverdueAlerts += await AckOverdueSweep.RunAsync(
                db, alerter, clock, logger, tenantId, target.Connection, ct);
        }

        return report;
    }

    /// <summary>
    /// One connection's inbound directory.
    ///
    /// <para>
    /// <b>One file's failure never costs the others their poll.</b> Each file is handled inside
    /// its own try: a connection whose directory holds one unreadable file must still have
    /// yesterday's acknowledgements applied, and the unreadable one is reported where an operator
    /// will see it.
    /// </para>
    /// </summary>
    private static async Task PollConnectionAsync(
        IntegrationDbContext db,
        IIntegrationFileTransport transport,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        IntegrationConnection connection,
        InboundBatchPollReport report,
        CancellationToken ct)
    {
        var listed = await transport.ListInboundAsync(connection, ct);

        if (listed.IsFailure)
        {
            // A host that is down or a directory that does not exist is an outcome a batch job
            // records and the next run retries — never an exception that fails the whole tenant's
            // sweep with a stack trace nobody maps back to a connection.
            logger.LogWarning(
                "Inbound directory of connection {ConnectionId} could not be listed: {Code} {Detail}.",
                connection.Id, listed.Code, listed.Detail);

            return;
        }

        report.Listed += listed.Value.Count;

        foreach (var fileName in listed.Value)
        {
            try
            {
                await ProcessFileAsync(
                    db, transport, projector, clock, logger, tenantId, connection, fileName,
                    report, ct);
            }
            catch (Exception ex)
            {
                // The unit of work of whichever step threw is half-applied and must not ride along
                // into the next file's saves.
                db.ChangeTracker.Clear();

                report.Unreadable++;
                report.Files.Add(new InboundFileReport(
                    fileName, null, InboundFileOutcome.Unreadable,
                    InboundBatchCodes.HeaderUnreadable, ex.Message));

                logger.LogError(
                    ex, "Inbound file {FileName} of connection {ConnectionId} threw; the other "
                    + "files of the directory are unaffected.", fileName, connection.Id);
            }
        }
    }

    /// <summary>
    /// One file: verify, then apply, then record.
    ///
    /// <para>
    /// <b>The <c>integration_batch_file</c> row is written LAST on the success path, and that
    /// order is load-bearing.</b> The row IS the "already processed" memory of criterion 1, so
    /// writing it first and applying afterwards would leave a crash halfway through a file
    /// recorded as done — its remaining commands never closed, and the next poll skipping the
    /// file for ever. Written last, a crash leaves no row, the file is read again, and each line
    /// that already landed is reported as "not Batched" and skipped. At-least-once delivery with
    /// idempotent lines, which is the only shape that survives a worker being killed.
    /// </para>
    /// </summary>
    private static async Task ProcessFileAsync(
        IntegrationDbContext db,
        IIntegrationFileTransport transport,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        IntegrationConnection connection,
        string fileName,
        InboundBatchPollReport report,
        CancellationToken ct)
    {
        var fetched = await transport.GetInboundAsync(
            connection, fileName, InboundBatchFileFormat.MaxFileBytes, ct);

        if (fetched.IsFailure)
        {
            report.Unreadable++;
            report.Files.Add(new InboundFileReport(
                fileName, null, InboundFileOutcome.Unreadable, fetched.Code, fetched.Detail));

            logger.LogWarning(
                "Inbound file {FileName} of connection {ConnectionId} could not be read: "
                + "{Code} {Detail}.", fileName, connection.Id, fetched.Code, fetched.Detail);

            return;
        }

        // An envelope we cannot read is not attributable to a sequence, so there is no row to
        // record it under and nothing to skip it by next time. It is left in place and reported:
        // a file in our directory that is not one of our files is a question for a human, and
        // inventing a sequence for it would let it collide with a real one.
        InboundBatchHeader? header = null;
        string? failure = null;

        // The two steps are separate because a file with no line terminator at all has no header
        // line to diagnose: it is one long first line, and saying so is more useful than letting
        // the header parser report a field count.
        if (!InboundBatchFileFormat.TrySplit(fetched.Value, out var headerLine, out var body))
            failure = "The file has no line terminator, so it carries no header line.";
        else if (InboundBatchFileFormat.TryParseHeader(headerLine, out var parsedHeader, out failure))
            header = parsedHeader;

        if (header is null)
        {
            report.Unreadable++;
            report.Files.Add(new InboundFileReport(
                fileName, null, InboundFileOutcome.Unreadable,
                InboundBatchCodes.HeaderUnreadable, failure));

            logger.LogWarning(
                "Inbound file {FileName} of connection {ConnectionId} has no readable SANKORE "
                + "batch header: {Failure}", fileName, connection.Id, failure);

            return;
        }

        // ── Criterion 1: the sequence ───────────────────────────────────────
        // "Already processed" is the SEQUENCE, in the database — ux_integration_batch_file_sequence
        // is (tenant, connection, direction, sequence_no) — and not a filename convention: a
        // sender that renames a file, or re-deposits it under another name, must not be able to
        // make us apply it twice.
        var recorded = await db.BatchFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connection.Id
                     && f.Direction == BatchDirection.In
                     && f.SequenceNo == header.SequenceNo)
            .Select(f => new { f.Status, f.FileName })
            .FirstOrDefaultAsync(ct);

        if (recorded is not null)
        {
            // Any recorded status, not only Processed. Processed means applied; Failed means
            // already refused and reported, and re-reading it every minute would be a hot loop
            // over a file only a human can fix — while a second row for the same sequence would
            // lose on the unique index and fail the whole sweep.
            report.Skipped++;
            report.Files.Add(new InboundFileReport(
                fileName, header.SequenceNo, InboundFileOutcome.Skipped, null,
                $"Sequence {header.SequenceNo} is already recorded as {recorded.Status} "
                + $"(file '{recorded.FileName}')."));

            logger.LogDebug(
                "Inbound file {FileName} skipped: sequence {SequenceNo} of connection "
                + "{ConnectionId} is already {Status}.",
                fileName, header.SequenceNo, connection.Id, recorded.Status);

            return;
        }

        var highestProcessed = await db.BatchFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connection.Id
                     && f.Direction == BatchDirection.In
                     && f.Status == BatchFileStatus.Processed)
            .MaxAsync(f => (long?)f.SequenceNo, ct);

        // A sequence BELOW one already applied is a different problem from a duplicate, and it is
        // reported rather than applied: it is either a file the sender re-numbered or one that
        // arrived out of order after we had moved on, and applying it would run yesterday's
        // decisions over today's state. The code is the contract's own.
        if (highestProcessed is { } highest && header.SequenceNo < highest)
        {
            await RefuseAsync(
                db, clock, tenantId, connection, fileName, header, fetched.Value.Length,
                IntegrationErrors.BatchSequenceOutOfOrder,
                $"Sequence {header.SequenceNo} is below {highest}, already processed on this "
                + "connection.", report, logger, ct);

            return;
        }

        // ── Criterion 1: the checksum ───────────────────────────────────────
        // Over the PLAINTEXT body, as IntegrationBatchFile.ChecksumSha256 is defined. A mismatch
        // never applies the file: a truncated or corrupted acknowledgement file closes the wrong
        // commands, and the file is left in place because it is the only evidence of what the
        // external system actually sent.
        var computed = InboundBatchFileFormat.ComputeBodyChecksum(body.Span);

        if (!string.Equals(computed, header.BodyChecksumSha256, StringComparison.Ordinal))
        {
            await RefuseAsync(
                db, clock, tenantId, connection, fileName, header, fetched.Value.Length,
                IntegrationErrors.BatchChecksumMismatch,
                $"The header declares {header.BodyChecksumSha256}; the body hashes to {computed}.",
                report, logger, ct);

            return;
        }

        // ── Criteria 2 and 4: apply ─────────────────────────────────────────
        var settings = (BatchCapableSettings)connection.Settings!;

        var parsed = InboundBatchFileReader.ReadBody(
            fileName, header, body.Span, settings.FileEncoding, settings.FieldSeparator);

        report.Lines.AddRange(parsed.Reports);

        var applied = header.Kind == InboundFileKind.Acknowledgement
            ? await ApplyAcknowledgementsAsync(
                db, clock, tenantId, connection, fileName, parsed, report, ct)
            : await ApplyExtractionsAsync(
                db, projector, clock, tenantId, connection, fileName, parsed, report, ct);

        // Recorded only now — see the method remarks for why this is last.
        var row = IntegrationBatchFile.Create(
            tenantId: tenantId,
            connectionId: connection.Id,
            direction: BatchDirection.In,
            sequenceNo: header.SequenceNo,
            fileName: fileName,
            checksumSha256: computed,
            recordCount: parsed.Records.Count,
            clock: clock,
            // No storage reference: the encrypted object store is the outbound half's, and the
            // file itself stays on the server, archived out of the polling directory. That archive
            // is the evidence of what we were sent; a second copy here would be a second place to
            // purge and a second place for personal data to outlive its retention.
            storageRef: null);

        row.MarkProcessed(clock);
        db.BatchFiles.Add(row);
        await db.SaveChangesAsync(ct);

        report.Processed++;
        report.Files.Add(new InboundFileReport(
            fileName, header.SequenceNo, InboundFileOutcome.Processed, null,
            $"{applied} of {parsed.Records.Count} record(s) applied."));

        LogLineReports(logger, fileName, parsed.Reports, report);

        // Archiving rather than deleting, and a failure to archive is not a failure of the file:
        // the sequence row above already stops it being applied twice.
        var archived = await transport.ArchiveInboundAsync(connection, fileName, ct);

        if (archived.IsFailure)
            logger.LogWarning(
                "Inbound file {FileName} of connection {ConnectionId} was applied but could not "
                + "be archived: {Code} {Detail}. Its sequence is recorded, so it will not be "
                + "applied again.", fileName, connection.Id, archived.Code, archived.Detail);
    }

    /// <summary>
    /// Records a refused file as <c>Failed</c> and applies nothing of it. The row is what stops
    /// the next poll re-reading a file only a human can fix.
    /// </summary>
    private static async Task RefuseAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        Guid tenantId,
        IntegrationConnection connection,
        string fileName,
        InboundBatchHeader header,
        int byteCount,
        string code,
        string detail,
        InboundBatchPollReport report,
        ILogger logger,
        CancellationToken ct)
    {
        var row = IntegrationBatchFile.Create(
            tenantId: tenantId,
            connectionId: connection.Id,
            direction: BatchDirection.In,
            sequenceNo: header.SequenceNo,
            fileName: fileName,
            // The checksum the file CLAIMS. The row has to carry one — a file without a checksum
            // is not evidence, says the factory — and what a refused file claimed is the fact an
            // investigation needs; the computed value is in the detail below.
            checksumSha256: header.BodyChecksumSha256,
            // Nothing was parsed, so nothing is claimed about the record count. Zero is honest
            // here in a way a guess from the byte count would not be.
            recordCount: 0,
            clock: clock,
            storageRef: null);

        row.MarkFailed($"{code}: {detail}", clock);
        db.BatchFiles.Add(row);
        await db.SaveChangesAsync(ct);

        report.Failed++;
        report.Files.Add(new InboundFileReport(
            fileName, header.SequenceNo, InboundFileOutcome.Failed, code, detail));

        logger.LogError(
            "Inbound file {FileName} ({ByteCount} byte(s)) of connection {ConnectionId} refused: "
            + "{Code} {Detail}. It is left in place and nothing of it was applied.",
            fileName, byteCount, connection.Id, code, detail);
    }

    private static async Task<int> ApplyAcknowledgementsAsync(
        IntegrationDbContext db,
        TimeProvider clock,
        Guid tenantId,
        IntegrationConnection connection,
        string fileName,
        ParsedInboundBody parsed,
        InboundBatchPollReport report,
        CancellationToken ct)
    {
        var applied = 0;

        foreach (var record in parsed.Records)
        {
            var outcome = await AcknowledgementApplier.ApplyAsync(
                db, clock, tenantId, connection, fileName, record, ct);

            if (outcome.Report is { } line)
            {
                report.Lines.Add(line);
                continue;
            }

            applied++;
            report.CommandsClosed++;

            if (outcome.Closed == CommandStatus.Succeeded) report.CommandsSucceeded++;
            else report.CommandsRejected++;
        }

        return applied;
    }

    private static async Task<int> ApplyExtractionsAsync(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        TimeProvider clock,
        Guid tenantId,
        IntegrationConnection connection,
        string fileName,
        ParsedInboundBody parsed,
        InboundBatchPollReport report,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projector);

        var applied = 0;

        foreach (var record in parsed.Records)
        {
            var line = await ExtractionApplier.ApplyAsync(
                db, projector, clock, tenantId, connection.Id, fileName, record, ct);

            if (line is not null)
            {
                report.Lines.Add(line);
                continue;
            }

            applied++;
            report.CustomersProjected++;
        }

        return applied;
    }

    /// <summary>
    /// How many reported lines reach the log before it becomes the noise it was meant to cut
    /// through. A file pointed at us by mistake can be a hundred thousand unparseable lines, and
    /// a warning per line would bury the one file name an operator needs to see.
    /// </summary>
    private const int MaxLoggedLineReports = 20;

    private static void LogLineReports(
        ILogger logger,
        string fileName,
        IReadOnlyList<InboundLineReport> parseReports,
        InboundBatchPollReport report)
    {
        var lines = report.Lines.Where(l => l.FileName == fileName).ToList();

        if (lines.Count == 0) return;

        foreach (var line in lines.Take(MaxLoggedLineReports))
            logger.LogWarning(
                "Inbound file {FileName} line {FileLine}: {Code} {Detail}",
                fileName, line.FileLine, line.Code, line.Detail);

        if (lines.Count > MaxLoggedLineReports)
            logger.LogWarning(
                "Inbound file {FileName}: {Total} line(s) reported in all, {Shown} shown "
                + "({Unparseable} of them unreadable records).",
                fileName, lines.Count, MaxLoggedLineReports, parseReports.Count);
    }
}
