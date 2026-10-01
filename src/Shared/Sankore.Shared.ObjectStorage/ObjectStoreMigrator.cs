namespace Sankore.Shared.ObjectStorage;

using Microsoft.Extensions.Logging;

/// <summary>
/// Copies every object under a prefix from one backend to another — the move from a mounted
/// volume to a bucket, and the way back if it has to be undone.
///
/// <para>
/// It is written to be run twice. A migration of real evidence will be interrupted: a deploy
/// window closes, a network blips, one object out of forty thousand fails. So nothing here is
/// a transaction and nothing accumulates in memory — a key already present at the destination is
/// skipped, a failure is recorded and the run continues, and starting again picks up where the
/// last one stopped. The alternative, a run that must complete or be redone from zero, is the
/// one that gets abandoned half-way and leaves documents in two places with nobody sure which.
/// </para>
///
/// <para>
/// It reads and writes ciphertext and never holds a key. KYC objects are encrypted by
/// <c>KycDocumentStore</c> before they reach a backend, so the bytes that move are already
/// unreadable; the migrator could not decrypt them if it tried, which is the property that lets
/// it run as an ops job rather than as something compliance has to supervise.
/// </para>
/// </summary>
public sealed class ObjectStoreMigrator(ILogger<ObjectStoreMigrator> logger)
{
    /// <summary>
    /// Copies <paramref name="keyPrefix"/> from <paramref name="source"/> to
    /// <paramref name="destination"/>.
    ///
    /// <para>
    /// <paramref name="verify"/> re-reads each object from the destination and compares it byte
    /// for byte. It doubles the reads and is on by default anyway: a truncated copy of an
    /// identity document is indistinguishable from tampering later on, because the AES-GCM tag
    /// fails either way — at which point the original is gone and nobody can tell which happened.
    /// </para>
    ///
    /// <para>
    /// <paramref name="deleteFromSource"/> turns the copy into a move. It only ever deletes an
    /// object that was verified at the destination during THIS run, never one that was merely
    /// found to be already there: "already present" is the resume path, and a key that exists at
    /// the destination for some other reason must not be grounds for destroying the original.
    /// Leave it off for the first pass and run a second one once the bucket has been checked.
    /// </para>
    /// </summary>
    public async Task<ObjectMigrationReport> MigrateAsync(
        IObjectBackend source,
        IObjectBackend destination,
        string keyPrefix,
        long maxBytes,
        bool verify = true,
        bool deleteFromSource = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var copied = 0;
        var skipped = 0;
        var failures = new List<ObjectMigrationFailure>();

        logger.LogInformation(
            "Migrating objects under prefix '{KeyPrefix}' (verify: {Verify}, delete source: {DeleteFromSource})",
            keyPrefix, verify, deleteFromSource);

        await foreach (var key in source.ListAsync(keyPrefix, ct))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await destination.GetAsync(key, maxBytes, ct) is not null)
                {
                    skipped++;
                    continue;
                }

                var content = await source.GetAsync(key, maxBytes, ct);
                if (content is null)
                {
                    // Listed and then unreadable: the object is larger than the ceiling, or it
                    // vanished between the two calls. Either way it is not copied, and silence
                    // would mean losing it.
                    failures.Add(new ObjectMigrationFailure(key, "listed but could not be read from the source"));
                    continue;
                }

                await destination.PutAsync(key, content, ct);

                if (verify)
                {
                    var readBack = await destination.GetAsync(key, maxBytes, ct);

                    if (readBack is null || !readBack.AsSpan().SequenceEqual(content))
                    {
                        failures.Add(new ObjectMigrationFailure(key, "copy did not read back identical"));
                        continue;
                    }
                }

                copied++;

                if (deleteFromSource) await source.DeleteAsync(key, ct);
            }
#pragma warning disable CA1031 // One bad object must not end the run; it is recorded and reported.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Failed to migrate object {ObjectKey}", key);
                failures.Add(new ObjectMigrationFailure(key, ex.Message));
            }
        }

        var report = new ObjectMigrationReport(copied, skipped, failures);

        logger.LogInformation(
            "Migration of '{KeyPrefix}' finished: {Copied} copied, {Skipped} already present, {Failed} failed",
            keyPrefix, report.Copied, report.AlreadyPresent, report.Failures.Count);

        return report;
    }
}

/// <param name="AlreadyPresent">
/// Objects the destination already held. On a first run this is zero; on a resumed one it is most
/// of them, which is how an operator tells a resume from a run that did nothing.
/// </param>
public sealed record ObjectMigrationReport(
    int Copied,
    int AlreadyPresent,
    IReadOnlyList<ObjectMigrationFailure> Failures)
{
    /// <summary>
    /// False while anything failed. The source must not be decommissioned on a report that is not
    /// clean — the failures name exactly which objects would be lost.
    /// </summary>
    public bool IsComplete => Failures.Count == 0;
}

public sealed record ObjectMigrationFailure(string ObjectKey, string Reason);
