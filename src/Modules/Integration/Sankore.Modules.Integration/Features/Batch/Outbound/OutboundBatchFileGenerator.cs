namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.Crypto;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Produces, deposits and purges the outbound file of one batch connection — criteria 2 to 5 of
/// INT-24.
///
/// <para>
/// <b>Generation, deposit and purge are three separate methods, called in that order by the
/// job.</b> Not one, and the split is the design: <see cref="GenerateAsync"/> is also reachable
/// from the dispatcher (through <see cref="OutboundBatchFileEnlister"/>), which runs inside
/// <c>TransactionBehavior</c> — so an SFTP transfer folded into it would hold a database
/// transaction open for the length of a network transfer. Deposit therefore belongs to the job
/// alone, and the file's <c>Generated</c> status is what carries the hand-off between them. It is
/// also what makes criterion 4 retryable for free: a transport failure changes nothing, and the
/// next run of the job finds the same <c>Generated</c> file and tries again.
/// </para>
///
/// <para>
/// <b>What goes into the file, and the status move that goes with it.</b> The eligible set is the
/// connection's open commands whose <c>CreatedAt</c> is at or before the cycle's cut-off (see
/// <see cref="OutboundBatchCycle"/>). Each is moved to <c>Batched</c> <b>in the same
/// SaveChanges as the file row</b>, which is the only arrangement that cannot drift: a command
/// written into the file but left <c>Pending</c> would be sent again in the next cycle — the CBS
/// applying one customer twice — and a command marked <c>Batched</c> against a file that does not
/// contain it would wait for an acknowledgement that can never come. One transaction, both facts,
/// or neither.
/// </para>
///
/// <para>
/// <b>The seed.</b> When the dispatcher is the caller it has already claimed one command
/// (<c>Sending</c>, with a live claim) and will perform that command's own <c>MarkBatched</c>
/// itself — so the seed is included in the FILE and excluded from the status moves made here.
/// Without the seed parameter, a generation driven by the dispatcher would either skip the very
/// command that triggered it or fight the handler over its transition. A seed that is not
/// eligible is refused rather than silently dropped, because the handler would otherwise mark it
/// against a file whose content does not mention it.
/// </para>
///
/// <para>
/// Every other <c>Sending</c> command is left alone unless its claim has expired: it belongs to a
/// worker that is mid-call, and taking it over is how a write gets made twice.
/// </para>
/// </summary>
internal sealed class OutboundBatchFileGenerator(
    IntegrationDbContext db,
    IBatchFileStore store,
    IIntegrationFileTransport transport,
    IOutboundBatchFormatter formatter,
    [FromKeyedServices(IntegrationFieldProtection.Key)] IFieldEncryptor encryptor,
    TimeProvider clock,
    ILogger<OutboundBatchFileGenerator> logger)
{
    /// <summary>
    /// Ceiling on how many commands one file carries. A batch cycle is a day of one tenant's
    /// writes, so the normal figure is tens; five thousand is the point at which something has
    /// gone wrong — a replayed import, a loop — and producing a file the CBS will choke on is
    /// worse than producing the first five thousand and leaving the rest for the next cycle,
    /// which is what this does. Commands are taken in order, so nothing overtakes anything.
    /// </summary>
    private const int MaxRecordsPerFile = 5_000;

    // ── Criterion 2 and 3: generation ───────────────────────────────────────

    /// <summary>
    /// Produces the outbound file of the cycle <paramref name="connection"/> is currently in, or
    /// reports that there is nothing to produce.
    /// </summary>
    internal async Task<IntegrationResult<OutboundBatchGeneration>> GenerateAsync(
        Guid tenantId, IntegrationConnection connection, Guid? seedCommandId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Settings is not BatchCapableSettings settings)
            return IntegrationResult.Technical<OutboundBatchGeneration>(
                IntegrationErrors.SettingsInvalid,
                $"Connection {connection.Id} is of kind {connection.Kind}, whose settings carry no "
                + "batch coordinates; it cannot produce an outbound file.");

        // Resolved before anything is read, so an unknown code page costs no work and — more to
        // the point — never produces a file. A file written in the wrong encoding is not rejected
        // by a machine; it is read as mojibake and refused hours later by a human.
        if (!OutboundBatchEncoding.TryResolve(settings.FileEncoding, out var encoding, out var encodingError))
            return IntegrationResult.Technical<OutboundBatchGeneration>(
                IntegrationErrors.SettingsInvalid, encodingError);

        var now = clock.GetUtcNow();
        var cutOff = OutboundBatchCycle.CurrentCutOff(now, settings.CutOffTime);

        var commands = await EligibleAsync(tenantId, connection.Id, cutOff, now, seedCommandId, ct);

        if (seedCommandId is { } seed && commands.All(c => c.Id != seed))
        {
            // The command that triggered this generation is not eligible for the current cycle —
            // it was created after the cut-off. Transient and not a rejection: it is a perfectly
            // valid command whose turn has not come, and it leaves with the next file.
            var next = OutboundBatchCycle.NextCutOffAfter(now, settings.CutOffTime);

            // BatchCycleNotDue and not Unavailable: the dispatcher branches on this code to defer
            // without spending an attempt. Reported as Unavailable, eight retries would be burnt
            // in about three hours and a command created in the morning would be Rejected before
            // its evening file existed — on a daily cycle, that is most commands.
            //
            // The instant is in the detail in round-trippable form so the handler can parse it
            // back rather than recomputing a cut-off it has no settings for.
            return IntegrationResult.Transient<OutboundBatchGeneration>(
                IntegrationErrors.BatchCycleNotDue,
                $"{next:O}|This command was created after the current batch cut-off; it leaves "
                + $"with the file generated at {next:u} (cut-off {settings.CutOffTime:HH\\:mm} UTC).");
        }

        if (commands.Count == 0)
        {
            logger.LogDebug(
                "No command of connection {ConnectionId} is eligible for the batch cycle "
                + "ending {CutOff:u}; no file generated.",
                connection.Id, cutOff);

            return IntegrationResult.Ok(OutboundBatchGeneration.Nothing);
        }

        var records = commands.Select(ToRecord).ToList();

        var text = formatter.Render(
            new OutboundBatchContext(connection.Id, connection.Kind, settings.FieldSeparator, cutOff),
            records);

        var plaintext = encoding.GetBytes(text);

        // The checksum is over the PLAINTEXT, as IntegrationBatchFile requires: the stored object
        // is encrypted, so a checksum of the ciphertext would change with every re-encryption and
        // would say nothing about what the external system received. Over the ENCODED bytes and
        // not over the string, because the encoding is part of what was sent.
        var checksum = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();

        // Stored before the row is committed, so the row never points at an object that does not
        // exist. The reverse order would leave a Generated file whose content cannot be read and
        // therefore cannot be deposited — unrecoverable without a manual purge. A committed row
        // that fails afterwards leaves an orphaned object instead, which costs disk and nothing
        // else.
        string storageRef;
        try
        {
            storageRef = await store.StoreAsync(tenantId, plaintext, ct);
        }
        catch (Exception ex) when (ex is Sankore.Shared.Kernel.DomainException or InvalidOperationException)
        {
            // A missing storage key, or a file over the ceiling. Technical: retrying cannot fix
            // either, and the message names the setting.
            logger.LogError(
                ex, "Could not store the outbound batch object of connection {ConnectionId}",
                connection.Id);

            return IntegrationResult.Technical<OutboundBatchGeneration>(
                IntegrationErrors.SettingsInvalid, ex.Message);
        }

        // The ids that are IN the rendered content. The commit below attaches exactly these and
        // refuses if the set has moved — see ReloadAsync.
        var renderedIds = commands.Select(c => c.Id).ToList();

        var generation = await ClaimSequenceAndCommitAsync(
            tenantId, connection, cutOff, storageRef, checksum, renderedIds,
            seedCommandId, now, ct);

        if (generation.IsFailure)
        {
            // The row never landed, so the object is orphaned. Removed here rather than left for a
            // sweeper: nothing else knows this reference exists, since it was never written down.
            await SafeDeleteAsync(tenantId, storageRef, ct);
            return generation;
        }

        logger.LogInformation(
            "Generated outbound batch file {FileName} for connection {ConnectionId}: sequence "
            + "{SequenceNo}, {RecordCount} record(s), {SizeBytes} bytes, checksum {Checksum}.",
            generation.Value.FileName, connection.Id, generation.Value.SequenceNo,
            generation.Value.RecordCount, plaintext.Length, generation.Value.ChecksumSha256);

        return generation;
    }

    /// <summary>
    /// The commands this cycle's file carries, in the order it carries them.
    ///
    /// <para>
    /// Ordered by <c>(EntityType, CrmId, CreatedAt, Id)</c> — INT-06's per-entity ordering,
    /// preserved INSIDE the file. It is load-bearing for the same reason it is in the dispatcher:
    /// an <c>UpdateCustomer</c> line read before the <c>CreateCustomer</c> it amends fails as
    /// "entity not found". <c>CreatedAt</c> alone is not a total order — two commands created in
    /// one transaction share a timestamp to the tick — so <c>Id</c> breaks the tie
    /// deterministically.
    /// </para>
    ///
    /// <para>
    /// <c>AsTracking</c> because the status moves happen on these instances;
    /// <c>IgnoreQueryFilters</c> with an explicit tenant predicate because this runs in a job,
    /// where the ambient tenant is not necessarily the one being processed.
    /// </para>
    /// </summary>
    private Task<List<IntegrationCommand>> EligibleAsync(
        Guid tenantId,
        Guid connectionId,
        DateTimeOffset cutOff,
        DateTimeOffset now,
        Guid? seedCommandId,
        CancellationToken ct)
        => db.Commands
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.ConnectionId == connectionId

                     // Criterion 2's gate, and the whole of "at the cut-off and not before".
                     && c.CreatedAt <= cutOff
                     && (c.Status == CommandStatus.Pending
                         || (c.Status == CommandStatus.RetryScheduled
                             && (c.NextAttemptAt == null || c.NextAttemptAt <= now))

                         // Sending: the seed, which the caller already claimed, or a claim left
                         // behind by a worker that died. A live claim belongs to a worker that is
                         // mid-call and must not be taken over.
                         || (c.Status == CommandStatus.Sending
                             && (c.Id == seedCommandId
                                 || (c.NextAttemptAt != null && c.NextAttemptAt <= now)))))
            .OrderBy(c => c.EntityType)
            .ThenBy(c => c.CrmId)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(MaxRecordsPerFile)
            .ToListAsync(ct);

    /// <summary>
    /// The same eligibility predicate, narrowed to a known set of ids.
    ///
    /// <para>
    /// A <see cref="List{T}"/> and not an array: in .NET 10 an array's <c>Contains</c> binds to
    /// the <c>ReadOnlySpan&lt;T&gt;</c> extension and no longer translates to SQL, which would
    /// silently turn this into a client-side filter over the whole table. A documented pitfall of
    /// this repository.
    /// </para>
    /// </summary>
    private Task<List<IntegrationCommand>> ReloadAsync(
        Guid tenantId,
        Guid connectionId,
        List<Guid> commandIds,
        DateTimeOffset cutOff,
        DateTimeOffset now,
        Guid? seedCommandId,
        CancellationToken ct)
        => db.Commands
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.ConnectionId == connectionId
                     && commandIds.Contains(c.Id)
                     && c.CreatedAt <= cutOff
                     && (c.Status == CommandStatus.Pending
                         || (c.Status == CommandStatus.RetryScheduled
                             && (c.NextAttemptAt == null || c.NextAttemptAt <= now))
                         || (c.Status == CommandStatus.Sending
                             && (c.Id == seedCommandId
                                 || (c.NextAttemptAt != null && c.NextAttemptAt <= now)))))
            .ToListAsync(ct);

    /// <summary>
    /// Writes the file row and the commands' status moves in one transaction, retrying on the
    /// sequence index.
    ///
    /// <para>
    /// The CONTENT is rendered before this and does not depend on the sequence — which is why a
    /// retry here costs nothing and the stored object is written once. That is a constraint on
    /// <see cref="IOutboundBatchFormatter"/> and it is stated in its contract: a format that puts
    /// the sequence number in a header would have to move the render inside this loop.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<OutboundBatchGeneration>> ClaimSequenceAndCommitAsync(
        Guid tenantId,
        IntegrationConnection connection,
        DateTimeOffset cutOff,
        string storageRef,
        string checksum,
        List<Guid> renderedCommandIds,
        Guid? seedCommandId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= BatchSequenceAllocation.MaxAttempts; attempt++)
        {
            var sequenceNo = await NextSequenceAsync(tenantId, connection.Id, ct);
            var fileName = BuildFileName(sequenceNo, cutOff, formatter.FileExtension);

            // Re-read on every attempt, and re-read EXACTLY the commands the content mentions:
            // a previous attempt's failed SaveChanges left the change tracker holding instances
            // whose state no longer matches the database.
            var commands = await ReloadAsync(
                tenantId, connection.Id, renderedCommandIds, cutOff, now, seedCommandId, ct);

            if (commands.Count != renderedCommandIds.Count)
            {
                // The eligible set moved between the render and the commit — another run took
                // some of these commands, or one was inserted by a transaction that committed
                // late. Abandoning is the only correct answer: attaching a command the content
                // does not mention would leave it waiting for an acknowledgement that can never
                // arrive, and writing a file whose record count disagrees with its rows would
                // make the row's evidence wrong.
                db.ChangeTracker.Clear();

                logger.LogInformation(
                    "The eligible command set of connection {ConnectionId} changed between "
                    + "rendering and committing ({Expected} rendered, {Found} still eligible); "
                    + "abandoning this file, the next pass regenerates it.",
                    connection.Id, renderedCommandIds.Count, commands.Count);

                return IntegrationResult.Transient<OutboundBatchGeneration>(
                    IntegrationErrors.Unavailable,
                    "The set of commands owed to this batch cycle changed while the file was "
                    + "being written; it will be regenerated.");
            }

            var file = IntegrationBatchFile.Create(
                tenantId: tenantId,
                connectionId: connection.Id,
                direction: BatchDirection.Out,
                sequenceNo: sequenceNo,
                fileName: fileName,
                checksumSha256: checksum,
                recordCount: renderedCommandIds.Count,
                clock: clock,
                storageRef: storageRef);

            db.BatchFiles.Add(file);

            foreach (var command in commands)
            {
                // The seed's own move belongs to ExecuteIntegrationCommandHandler, which holds it
                // claimed and will call MarkBatched with the id this method returns.
                if (command.Id == seedCommandId) continue;

                // Pending and RetryScheduled must pass through Sending: that is the transition
                // table, and BeginSending is also what consumes the attempt, so a command that
                // keeps failing to be written into a file does not retry for ever.
                if (command.Status != CommandStatus.Sending) command.BeginSending(clock);

                command.MarkBatched(file.Id, clock);
            }

            try
            {
                await db.SaveChangesAsync(ct);

                return IntegrationResult.Ok(new OutboundBatchGeneration(
                    Generated: true,
                    FileId: file.Id,
                    SequenceNo: sequenceNo,
                    RecordCount: renderedCommandIds.Count,
                    ChecksumSha256: file.ChecksumSha256,
                    FileName: fileName));
            }
            catch (DbUpdateException ex) when (ex.IsSequenceCollision())
            {
                // Another run took this number between the read and the insert. The abandoned
                // number leaves a gap, which is the benign half of the sequence's purpose — see
                // BatchSequenceAllocation.
                db.ChangeTracker.Clear();

                logger.LogInformation(
                    "Sequence {SequenceNo} of connection {ConnectionId} was taken concurrently; "
                    + "retrying with the next one (attempt {Attempt} of {MaxAttempts}).",
                    sequenceNo, connection.Id, attempt, BatchSequenceAllocation.MaxAttempts);
            }
        }

        logger.LogError(
            "Could not allocate an outbound sequence number for connection {ConnectionId} after "
            + "{MaxAttempts} attempts.", connection.Id, BatchSequenceAllocation.MaxAttempts);

        // Transient: losing the race repeatedly means other runs are succeeding, so the commands
        // are not stuck — the next cycle picks up whatever is left.
        return IntegrationResult.Transient<OutboundBatchGeneration>(
            IntegrationErrors.Unavailable,
            "The outbound sequence number could not be allocated; another run of the same "
            + "connection is generating. It will be retried.");
    }

    /// <summary>
    /// <c>MAX(sequence_no) + 1</c> for this connection and direction — a first guess, not the
    /// guarantee. See <see cref="BatchSequenceAllocation"/>.
    ///
    /// <para>
    /// Per DIRECTION as well as per connection, because the unique index is: inbound files have
    /// their own series, and sharing one counter would make an outbound gap appear every time an
    /// acknowledgement arrived.
    /// </para>
    /// </summary>
    private async Task<long> NextSequenceAsync(Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        var highest = await db.BatchFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connectionId
                     && f.Direction == BatchDirection.Out)
            .MaxAsync(f => (long?)f.SequenceNo, ct);

        // Starts at 1: IntegrationBatchFile.Create refuses zero, because "a sequence number
        // starts at 1" and a CBS cannot tell a zero from an unset field.
        return (highest ?? 0) + 1;
    }

    /// <summary>
    /// <c>{yyyyMMdd}-{sequence:D9}{extension}</c>, prefixed by the cycle's date.
    ///
    /// <para>
    /// The sequence is zero-padded so a directory listing sorts in order rather than
    /// lexicographically (file 10 before file 2 is how an operator reads a gap that is not there),
    /// and nine digits is more cycles than any institution will run. The date leads because an
    /// operator looking at a deposit directory asks "which day" first. Neither the tenant nor the
    /// connection id appears: the directory is already per-connection, and a tenant identifier in
    /// a file name on somebody else's server is an identifier we did not need to give them.
    /// </para>
    /// </summary>
    private static string BuildFileName(long sequenceNo, DateTimeOffset cutOff, string extension)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{cutOff:yyyyMMdd}-{sequenceNo:D9}{extension}");

    /// <summary>
    /// One command as the formatter sees it, payload decrypted.
    ///
    /// <para>
    /// This is the ONE place in the module that decrypts a command payload, and it does so because
    /// the whole purpose of the file is that the CBS can read it — the protection the payload
    /// column carries is against the data sitting at rest here while the external system is down,
    /// not against the external system receiving it. Nothing on this path logs a value.
    /// </para>
    /// </summary>
    private OutboundBatchRecord ToRecord(IntegrationCommand command)
        => new(
            command.Id,
            command.CommandType,
            command.EntityType,
            command.CrmId,
            command.IdempotencyKey,
            command.CreatedAt,
            FlattenPayload(command));

    /// <summary>
    /// Top-level payload fields as text.
    ///
    /// <para>
    /// Top-level only, matching what <c>CommandPayloadProtector</c> records as the field names:
    /// a nested value is written back as compact JSON in its own column rather than flattened,
    /// so a beneficiary list stays one cell instead of becoming one column per beneficiary —
    /// which would make the column set of a file depend on how many beneficiaries it happened to
    /// carry.
    /// </para>
    ///
    /// <para>
    /// An unreadable payload yields an EMPTY field set and a warning, never an exception. The
    /// command's identity, type and CRM reference still reach the file, which is what lets an
    /// operator see that something was owed for that customer and could not be rendered —
    /// whereas a throw here would abandon the whole cycle's file over one bad row.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, string> FlattenPayload(IntegrationCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.PayloadEncrypted))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        string? json;
        try
        {
            json = encryptor.Decrypt(command.PayloadEncrypted);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException
                                      or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(
                ex, "The payload of command {CommandId} could not be decrypted; it is written to "
                + "the batch file without its fields.", command.Id);

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(
                ex, "The payload of command {CommandId} is not readable JSON; it is written to the "
                + "batch file without its fields.", command.Id);

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (node is not JsonObject obj)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var fields = new Dictionary<string, string>(obj.Count, StringComparer.Ordinal);

        foreach (var (name, value) in obj) fields[name] = Text(value);

        return fields;
    }

    /// <summary>
    /// One JSON value as the file's text. <c>InvariantCulture</c> throughout, because a decimal
    /// written as <c>1250,50</c> on a fr-FR host is a different number to every parser that reads
    /// it — the lesson the three spreadsheet importers in this repo already carry.
    /// </summary>
    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),

        // An object or an array: kept whole, in one cell. See FlattenPayload.
        _ => value.ToJsonString(),
    };

    // ── Criterion 4: deposit ────────────────────────────────────────────────

    /// <summary>
    /// Deposits every <c>Generated</c> outbound file of this connection, oldest sequence first.
    ///
    /// <para>
    /// Covers the file this run just produced AND any earlier one whose transfer failed, which is
    /// what makes criterion 4 retryable without a retry mechanism of its own. Oldest first and
    /// <b>stopping at the first failure</b>: a CBS that applies files in order must not receive
    /// sequence 8 while 7 is still missing, since from its side that is indistinguishable from a
    /// gap it should refuse.
    /// </para>
    ///
    /// <para>
    /// The content is re-read from the store and <b>its checksum re-verified</b> before the
    /// transfer. Not belt-and-braces: the row's checksum is the only evidence of what was sent,
    /// so depositing bytes that no longer hash to it would make that evidence a lie. A mismatch
    /// fails the file rather than sending it.
    /// </para>
    /// </summary>
    internal async Task<OutboundBatchDeposit> DepositAsync(
        Guid tenantId, IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var pending = await db.BatchFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connection.Id
                     && f.Direction == BatchDirection.Out
                     && f.Status == BatchFileStatus.Generated)
            .OrderBy(f => f.SequenceNo)
            .ToListAsync(ct);

        var deposited = 0;
        var failed = 0;

        foreach (var file in pending)
        {
            if (file.StorageRef is null)
            {
                // Generated with no content: nothing to deposit and nothing to recover. Failed so
                // an operator sees it, rather than retried for ever.
                file.MarkFailed(
                    "The stored content of this file is missing; it cannot be deposited.", clock);

                failed++;
                break;
            }

            var content = await store.OpenAsync(tenantId, file.StorageRef, ct);

            if (content is null)
            {
                file.MarkFailed(
                    "The stored content of this file could not be read back; it cannot be "
                    + "deposited.", clock);

                failed++;
                break;
            }

            var checksum = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            if (!string.Equals(checksum, file.ChecksumSha256, StringComparison.Ordinal))
            {
                logger.LogError(
                    "Batch file {FileId} of connection {ConnectionId} no longer matches its "
                    + "recorded checksum; refusing to deposit it.", file.Id, connection.Id);

                file.MarkFailed(
                    "The stored content no longer matches the recorded checksum; refusing to "
                    + "deposit it.", clock);

                failed++;
                break;
            }

            var sent = await transport.PutAsync(connection, file.FileName, content, ct);

            if (sent.IsFailure)
            {
                // The file stays Generated — untouched, retryable. Deliberately NOT MarkFailed:
                // the transfer failing says nothing about the file, and a Failed row would have to
                // be revived by hand once the server came back.
                logger.LogWarning(
                    "Depositing batch file {FileName} of connection {ConnectionId} failed "
                    + "({Code}); it stays Generated and will be retried.",
                    file.FileName, connection.Id, sent.Code);

                failed++;
                break;
            }

            file.MarkSent(clock);
            deposited++;
        }

        if (deposited > 0 || failed > 0) await db.SaveChangesAsync(ct);

        return new OutboundBatchDeposit(deposited, failed, pending.Count);
    }

    // ── Criterion 5: purge after acknowledgement and retention ──────────────

    /// <summary>
    /// Deletes the CONTENT of acknowledged files past their retention delay, keeping the row.
    ///
    /// <para>
    /// Both conditions, and in that order. <b>Acknowledged</b> first: a file the CBS has not
    /// confirmed may still have to be re-deposited, and purging it would leave a write owed with
    /// no way to make it. <b>Then the delay</b>, counted from the acknowledgement rather than from
    /// generation, because the window in which somebody asks "what exactly did you send us on the
    /// 3rd" opens when the other side processes the file, not when we wrote it.
    /// </para>
    ///
    /// <para>
    /// What survives is the row: sequence number, file name, record count and checksum. That is
    /// the point of the purge rather than a deletion — an inspection a year later can confirm what
    /// was sent, and in what order, without the platform still holding a flat file of a thousand
    /// customers' identity data.
    /// </para>
    ///
    /// <para>
    /// A content delete that fails leaves the row <c>Acknowledged</c> and the next run tries
    /// again. The reverse — marking it purged and failing to delete — would record the data as
    /// gone while it was still on the volume, which is the one outcome a retention promise cannot
    /// survive.
    /// </para>
    /// </summary>
    internal async Task<int> PurgeAsync(
        Guid tenantId, IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Settings is not BatchCapableSettings settings) return 0;

        // A non-positive RetentionDays would purge an acknowledgement the instant it arrived, and
        // the entity's own default is 30 — so a misconfigured zero takes the default rather than
        // meaning "immediately", the same reading ConnectionSettings.SyncIntervalFor applies.
        var retentionDays = settings.RetentionDays > 0 ? settings.RetentionDays : 30;
        var threshold = clock.GetUtcNow().AddDays(-retentionDays);

        var expired = await db.BatchFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId
                     && f.ConnectionId == connection.Id
                     && f.Status == BatchFileStatus.Acknowledged
                     && f.StorageRef != null
                     && f.AckReceivedAt != null
                     && f.AckReceivedAt <= threshold)
            .OrderBy(f => f.SequenceNo)
            .ToListAsync(ct);

        var purged = 0;

        foreach (var file in expired)
        {
            var removed = await SafeDeleteAsync(tenantId, file.StorageRef!, ct);

            // A reference that holds nothing is already purged in substance — a volume replaced, a
            // bucket lifecycle rule, a previous half-finished run. Marking the row is then the
            // honest record, and leaving it unmarked would re-attempt it every single night.
            if (!removed)
                logger.LogInformation(
                    "Batch file {FileId} of connection {ConnectionId} held no content to purge; "
                    + "recording it as purged.", file.Id, connection.Id);

            file.MarkPurged();
            purged++;
        }

        if (purged > 0)
        {
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Purged the content of {Purged} acknowledged batch file(s) of connection "
                + "{ConnectionId} older than {RetentionDays} day(s); the rows are kept.",
                purged, connection.Id, retentionDays);
        }

        return purged;
    }

    /// <summary>
    /// Deletes an object without letting a storage fault escape. Used on the rollback path of a
    /// failed generation and on the purge, both of which have something better to do than fault:
    /// the generation already has a failure to report, and the purge has other files to get to.
    /// </summary>
    private async Task<bool> SafeDeleteAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        try
        {
            return await store.DeleteAsync(tenantId, storageRef, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            logger.LogWarning(
                ex, "Could not delete batch object {StorageRef} of tenant {TenantId}",
                storageRef, tenantId);

            return false;
        }
    }
}

/// <summary>
/// What one generation produced. <see cref="Generated"/> is <c>false</c> when there was nothing
/// eligible — a success, not a failure: a connection with no pending write owes no file, and
/// reporting that as an error would alert an administrator every cut-off on a quiet day.
/// </summary>
internal sealed record OutboundBatchGeneration(
    bool Generated,
    Guid FileId,
    long SequenceNo,
    int RecordCount,
    string ChecksumSha256,
    string FileName)
{
    internal static OutboundBatchGeneration Nothing { get; } =
        new(false, Guid.Empty, 0, 0, string.Empty, string.Empty);
}

/// <summary>What one deposit pass did, for the job's log line and for the tests.</summary>
internal sealed record OutboundBatchDeposit(int Deposited, int Failed, int Pending);
