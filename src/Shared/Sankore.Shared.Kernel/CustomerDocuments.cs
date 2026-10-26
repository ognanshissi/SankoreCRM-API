namespace Sankore.Shared.Kernel;

/// <summary>Whether the client gave the document or was given it.</summary>
public enum CustomerDocumentDirection
{
    /// <summary>Collected from the client: an identity document, a claim piece, a consent.</summary>
    Collected,

    /// <summary>Handed to the client: an information sheet, a policy document, a message.</summary>
    Delivered,
}

/// <summary>
/// One document a client gave or received, as the module that keeps it reports it to the client
/// record's « Documents » tab (product owner, 2026-10-26). Every module that holds a client's
/// documents answers in this one shape, so the client record lists them side by side without
/// knowing any module's tables.
/// </summary>
/// <param name="Key">Unique across sources: <c>{source}:{id}</c>.</param>
/// <param name="Source"><c>Kyc</c>, <c>Insurance</c>, <c>Project</c>, <c>Task</c> or <c>WhatsApp</c>.</param>
/// <param name="Kind">
/// What the document is, as a code a screen translates — readable without the right to open it,
/// unlike <paramref name="FileName"/>, which often carries a name or an identity number.
/// </param>
/// <param name="Status">The source's own word: a review verdict, a scan state, « sent ».</param>
/// <param name="Detail">A short complement: the channel a document was handed over by, a type.</param>
/// <param name="OriginType">
/// What the document hangs off — <c>KycFile</c>, <c>Holding</c>, <c>Policy</c>, <c>Claim</c>,
/// <c>Project</c>, <c>Task</c>, <c>Lead</c> — so a screen can link back to it.
/// </param>
/// <param name="ContentPath">
/// The existing API route (after <c>api/v1/</c>) that streams it, which keeps its own permission,
/// perimeter and access log. Null for a record with no file, such as a consent proof.
/// </param>
/// <param name="OpenPermissions">
/// The permissions that route accepts, any one of them — what « can open » is computed from.
/// </param>
public sealed record CustomerDocumentEntry(
    string Key,
    string Source,
    CustomerDocumentDirection Direction,
    string Kind,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    DateTimeOffset At,
    string? Status,
    string? Detail,
    string OriginType,
    Guid OriginId,
    string? OriginLabel,
    string? ContentPath,
    IReadOnlyList<string> OpenPermissions);
