namespace Sankore.Modules.Customers.Features.Duplicates;

/// <summary>
/// Error codes owned by the deduplication / merge zone only.
/// <para>
/// Everything shared with the rest of M01 lives in
/// <see cref="Sankore.Modules.Customers.Domain.CustomerErrors"/>; the two codes below are
/// local because they describe objects no other zone manipulates (a duplicate candidate row
/// and a phonetic-key backfill run). Same UPPER_SNAKE convention, same contract value: the
/// front-end localizes the string, never the HTTP status.
/// </para>
/// </summary>
internal static class DuplicatesErrors
{
    /// <summary>No duplicate candidate with that id inside the caller's tenant and agency perimeter.</summary>
    public const string DuplicateCandidateNotFound = "DUPLICATE_CANDIDATE_NOT_FOUND";
}
