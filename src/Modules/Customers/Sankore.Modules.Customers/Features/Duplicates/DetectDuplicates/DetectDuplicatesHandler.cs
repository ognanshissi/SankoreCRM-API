namespace Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Scores client pairs of one tenant and upserts the <see cref="DuplicateCandidate"/> rows a human
/// then reviews.
///
/// <para><b>No decryption, ever.</b> The comparable projection (<see cref="ClientMatchInput"/>)
/// carries the phonetic keys, the date-of-birth blind index, the identity-document blind index, the
/// agency and the parents' names — nothing that needs <c>IFieldEncryptor</c>. That is a hard
/// requirement of the US, asserted by a test that fails if <c>Decrypt</c> is called once during a
/// run.</para>
///
/// <para><b>Blocking is mandatory.</b> A full cross join is O(n²): 100 000 clients would mean five
/// billion comparisons. Instead each client is filed under blocking keys — each non-null phonetic
/// key, the date-of-birth blind index and the identity-document blind index — and pairs are only
/// scored inside a block. Cost drops to O(Σ bᵢ²) over blocks of size bᵢ, which is near-linear for
/// real name distributions, and a canonical pair key makes sure a pair filed under two different
/// blocks is still only scored once. The trade-off is explicit: two records that share NO blocking
/// key at all are never compared, which is exactly why the phonetic-key backfill
/// (<c>BackfillPhoneticKeysJob</c>) exists.</para>
/// </summary>
internal sealed class DetectDuplicatesHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    TimeProvider clock,
    ILogger<DetectDuplicatesHandler> logger)
    : IRequestHandler<DetectDuplicatesCommand, Result<DetectDuplicatesResult>>
{
    /// <summary>A block larger than this is logged: it means a blocking key has lost its selectivity.</summary>
    private const int LargeBlockWarningThreshold = 1_000;

    public async Task<Result<DetectDuplicatesResult>> Handle(
        DetectDuplicatesCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var now = clock.GetUtcNow();
        var threshold = await settings.GetIntAsync(tenantId, CustomerSettingKeys.DuplicateScoreThreshold, ct);

        // Archived and merged clients are out: the first is end-of-life, the second already points
        // at its survivor. Projection stays inside the database — only indexable, non-decryptable
        // columns are selected.
        var inputs = await db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Status != ClientStatus.Archived
                     && c.Status != ClientStatus.Merged)
            .Select(c => new ClientMatchInput(
                c.Id,
                c.PhoneticKeyPrimary,
                c.PhoneticKeySecondary,
                c.DateOfBirthBlindIndex,
                c.AgencyId,
                c.FatherName,
                c.MotherName,
                c.IdentityDocumentNumberBlindIndex))
            .ToListAsync(ct);

        if (inputs.Count < 2)
            return Result.Ok(new DetectDuplicatesResult(inputs.Count, 0, 0, 0, 0, 0));

        var byId = inputs.ToDictionary(i => i.ClientId);
        var blocks = BuildBlocks(inputs);

        foreach (var block in blocks.Where(b => b.Value.Count > LargeBlockWarningThreshold))
        {
            logger.LogWarning(
                "Duplicate detection: blocking key {BlockKey} holds {Size} clients for tenant {TenantId}; " +
                "comparison stays quadratic inside that block.",
                block.Key, block.Value.Count, tenantId);
        }

        // Existing rows for the tenant, tracked so Refresh()/MarkMerged() are picked up by SaveChanges.
        var existing = await db.DuplicateCandidates
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .ToListAsync(ct);

        var existingByPair = existing.ToDictionary(d => (d.ClientAId, d.ClientBId));

        var fingerprints = new Dictionary<Guid, string>(inputs.Count);
        var comparedPairs = new HashSet<(Guid, Guid)>();
        int compared = 0, created = 0, refreshed = 0, skipped = 0, failed = 0;

        foreach (var block in blocks.Values)
        {
            for (var i = 0; i < block.Count - 1; i++)
            {
                for (var j = i + 1; j < block.Count; j++)
                {
                    // Canonical pair key: ClientAId < ClientBId, the same ordering the unique index
                    // uses, so the pair is identified identically whatever block surfaced it.
                    var (aId, bId) = Canonical(block[i], block[j]);

                    if (!comparedPairs.Add((aId, bId)))
                        continue;

                    var a = byId[aId];
                    var b = byId[bId];
                    compared++;

                    // One pair must not be able to end the sweep. A tenant-wide run can hold
                    // millions of pairs, and a single row whose data breaks an aggregate invariant
                    // used to abort all of them: one client with two identical phonetic keys threw
                    // "A client cannot duplicate itself." and every other candidate in that run —
                    // created, refreshed or merely confirmed — was lost with it. A reviewer saw no
                    // duplicates at all, which reads exactly like "there are none".
                    //
                    // Only DomainException is caught, deliberately. It is the aggregate saying THIS
                    // pair is not admissible, which is per-pair by definition and safe to skip.
                    // Anything else — a cancellation, a broken DbContext, a bug in the scorer — is
                    // not a property of the pair, and swallowing it would turn a failed run into a
                    // quietly incomplete one, which is worse than a loud failure for a job whose
                    // whole output is "these are the duplicates".
                    //
                    // Nothing partial can be committed on this path: Detect() throws before the row
                    // is added to the context, and Refresh() is assignments plus Math.Clamp with no
                    // validation, so it cannot fail halfway through mutating a tracked entity. If
                    // Refresh ever gains a guard, the failed candidate must be detached here.
                    try
                    {
                        var score = ClientMatchScorer.Score(a, b);
                        if (score.Score < threshold)
                            continue;

                        var fingerprintA = Fingerprint(fingerprints, a);
                        var fingerprintB = Fingerprint(fingerprints, b);
                        var reasonsJson = SerializeReasons(score);

                        if (existingByPair.TryGetValue((aId, bId), out var candidate))
                        {
                            switch (candidate.Status)
                            {
                                // Already merged by a human: the pair is settled forever.
                                case DuplicateCandidateStatus.Merged:
                                    skipped++;
                                    continue;

                                // Explicitly rejected: do not nag the reviewer again as long as the
                                // compared data has not moved. A changed fingerprint on either side
                                // means the comparison is no longer the one that was rejected, so the
                                // pair legitimately comes back to review.
                                case DuplicateCandidateStatus.Rejected
                                    when candidate.FingerprintA == fingerprintA
                                      && candidate.FingerprintB == fingerprintB:
                                    skipped++;
                                    continue;

                                default:
                                    candidate.Refresh(score.Score, reasonsJson, fingerprintA, fingerprintB, now);
                                    refreshed++;
                                    continue;
                            }
                        }

                        var row = DuplicateCandidate.Detect(
                            tenantId, aId, bId, score.Score, reasonsJson, fingerprintA, fingerprintB, now);

                        db.DuplicateCandidates.Add(row);
                        existingByPair[(aId, bId)] = row;
                        created++;
                    }
                    catch (DomainException ex)
                    {
                        failed++;

                        // Client ids only — a Guid identifies the row to investigate without putting
                        // any of the personal data this feature never decrypts into a log.
                        logger.LogWarning(
                            ex,
                            "Duplicate detection: pair ({ClientAId}, {ClientBId}) rejected by the domain "
                            + "for tenant {TenantId}; skipped, the run continues.",
                            aId, bId, tenantId);
                    }
                }
            }
        }

        if (created > 0 || refreshed > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Duplicate detection for tenant {TenantId}: {Clients} client(s), {Pairs} pair(s) compared, " +
            "{Created} created, {Refreshed} refreshed, {Skipped} skipped, {Failed} failed (threshold {Threshold}).",
            tenantId, inputs.Count, compared, created, refreshed, skipped, failed, threshold);

        // A run that skipped pairs still succeeded, but silence would hide it: the counter is in the
        // result so the job logs it, and a non-zero value is a data problem to go and look at.
        if (failed > 0)
        {
            logger.LogWarning(
                "Duplicate detection for tenant {TenantId} completed with {Failed} pair(s) skipped on "
                + "a domain error; their candidates were neither created nor refreshed.",
                tenantId, failed);
        }

        return Result.Ok(new DetectDuplicatesResult(inputs.Count, compared, created, refreshed, skipped, failed));
    }

    /// <summary>
    /// Files every client under each of its blocking keys. Null / blank keys are never a block:
    /// otherwise every client missing a date of birth would land in one giant bucket and bring the
    /// cross join back through the window.
    /// </summary>
    private static Dictionary<string, List<Guid>> BuildBlocks(IReadOnlyList<ClientMatchInput> inputs)
    {
        var blocks = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);

        // A client must appear at most ONCE per block. Two of its keys can legitimately be equal
        // — a client whose given and family names fold to the same phonetic key, which West
        // African naming makes routine ("Kouassi Kouassi", and the calculator folds ou/w, dj/j,
        // kh/k and doubled letters before Double Metaphone, so near-misses collide too) — and
        // both phonetic keys share the "PH:" namespace on purpose, so that client used to be
        // appended to the same bucket twice. The pair loop below then read two positions holding
        // the same id and asked DuplicateCandidate.Detect to pair a client with itself, which it
        // rightly refuses: one such client aborted the whole tenant's run with
        // "A client cannot duplicate itself."
        var seen = new HashSet<(string BlockKey, Guid ClientId)>();

        void Add(string? key, string prefix, Guid clientId)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            var blockKey = prefix + key;

            // Deduplicated here rather than skipped at the call site: the invariant belongs to the
            // block, so it keeps holding if another key is ever filed under an existing namespace.
            if (!seen.Add((blockKey, clientId)))
                return;

            if (!blocks.TryGetValue(blockKey, out var bucket))
                blocks[blockKey] = bucket = [];

            bucket.Add(clientId);
        }

        foreach (var input in inputs)
        {
            // Both phonetic keys share one namespace so a swapped first/last name still meets its
            // twin inside the same block — ClientMatchScorer scores that cross match.
            Add(input.PhoneticKeyPrimary, "PH:", input.ClientId);
            Add(input.PhoneticKeySecondary, "PH:", input.ClientId);
            Add(input.DateOfBirthBlindIndex, "DOB:", input.ClientId);
            // An identical identity document is the strongest single signal there is; blocking on it
            // catches the pair even when the two records spell the name differently.
            Add(input.IdentityDocumentNumberBlindIndex, "DOC:", input.ClientId);
        }

        return blocks;
    }

    private static (Guid A, Guid B) Canonical(Guid x, Guid y) =>
        x.CompareTo(y) <= 0 ? (x, y) : (y, x);

    private static string Fingerprint(Dictionary<Guid, string> cache, ClientMatchInput input)
    {
        if (cache.TryGetValue(input.ClientId, out var cached))
            return cached;

        var value = ClientMatchScorer.Fingerprint(input);
        cache[input.ClientId] = value;
        return value;
    }

    private static string SerializeReasons(MatchScore score)
    {
        var reasons = score.Signals
            .Where(s => s.Matched)
            .Select(s => new DuplicateReasonDto(s.Key, s.Weight))
            .ToList();

        return JsonSerializer.Serialize(reasons);
    }
}
