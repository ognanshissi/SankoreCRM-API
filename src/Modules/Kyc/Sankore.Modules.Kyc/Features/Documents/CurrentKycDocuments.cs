namespace Sankore.Modules.Kyc.Features.Documents;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;

/// <summary>
/// The current image of each kind on a file — the latest upload, the registry being append-only —
/// and what the file's validation does to it.
/// </summary>
internal static class CurrentKycDocuments
{
    /// <summary>True for every row that is the newest upload of its kind on its file.</summary>
    internal static HashSet<Guid> CurrentIds(IEnumerable<KycDocument> documents)
        => [.. documents
            .GroupBy(d => (d.KycFileId, d.Kind))
            .Select(g => g.OrderByDescending(d => d.UploadedAt).First().Id)];

    /// <summary>
    /// The final approval accepts the current images nobody reviewed, in the approver's name
    /// (product owner, 2026-11-02): a validator who signs the file has accepted its evidence, and a
    /// validated file whose pieces still read « En attente » told the client record the opposite.
    /// A refused current image stays refused; older retakes are left as they are — the client
    /// record shows them « Remplacé ». The caller saves, in the approval's own unit of work.
    /// </summary>
    internal static async Task AcceptPendingAsync(
        KycDbContext db, Guid kycFileId, Guid approvedBy, TimeProvider clock, CancellationToken ct)
    {
        var documents = await db.KycDocuments.AsTracking()
            .Where(d => d.KycFileId == kycFileId)
            .ToListAsync(ct);

        var current = CurrentIds(documents);
        foreach (var document in documents.Where(d => current.Contains(d.Id)
                                                      && d.ReviewDecision == KycDocumentReviewDecision.Pending))
            document.Review(KycDocumentReviewDecision.Accepted, approvedBy, clock);
    }
}
