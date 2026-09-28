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
            return Result.Ok(new DetectDuplicatesResult(inputs.Count, 0, 0, 0, 0));

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
        int compared = 0, created = 0, refreshed = 0, skipped = 0;

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
            }
        }

        if (created > 0 || refreshed > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Duplicate detection for tenant {TenantId}: {Clients} client(s), {Pairs} pair(s) compared, " +
            "{Created} created, {Refreshed} refreshed, {Skipped} skipped (threshold {Threshold}).",
            tenantId, inputs.Count, compared, created, refreshed, skipped, threshold);

        return Result.Ok(new DetectDuplicatesResult(inputs.Count, compared, created, refreshed, skipped));
    }

    /// <summary>
    /// Files every client under each of its blocking keys. Null / blank keys are never a block:
    /// otherwise every client missing a date of birth would land in one giant bucket and bring the
    /// cross join back through the window.
    /// </summary>
    private static Dictionary<string, List<Guid>> BuildBlocks(IReadOnlyList<ClientMatchInput> inputs)
    {
        var blocks = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);

        void Add(string? key, string prefix, Guid clientId)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            var blockKey = prefix + key;
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
