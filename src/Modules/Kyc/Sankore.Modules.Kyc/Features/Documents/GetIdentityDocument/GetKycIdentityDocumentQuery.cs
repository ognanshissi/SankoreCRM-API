namespace Sankore.Modules.Kyc.Features.Documents.GetIdentityDocument;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// What the OCR panel shows: the fields the machine read, how sure it was of each, and the
/// machine-readable zone — so an agent can correct a misread value.
/// </summary>
internal sealed record GetKycIdentityDocumentQuery(Guid KycFileId)
    : IRequest<Result<KycIdentityDocumentDto>>;

/// <param name="MaskedNumber">
/// The document number, masked: first two and last two characters kept
/// (<c>"CI•••••••42"</c>). Masked and not omitted because an agent has to recognise WHICH document
/// they are looking at, and not in clear because revealing the number is its own permission
/// (<c>kyc:document:reveal</c>) and its own audited slice. Null when the stored value cannot be
/// decrypted — a wrong or rotated key — rather than failing the whole panel.
/// </param>
/// <param name="Fields">
/// The OCR reading, keyed by the service's own field names. **The document number is not among
/// them**, and never was: it is stripped before the row is written, because that column is plain
/// jsonb and leaving it there would store in clear the exact value the encrypted column beside it
/// exists to protect.
/// </param>
/// <param name="FieldConfidences">
/// 0..1 per field of <paramref name="Fields"/>, keyed alike. A field may be absent — the service
/// does not grade every one — and the whole map is absent for a document read before this was
/// stored, which no backfill can repair. A caller must render "unknown", not "certain".
/// </param>
/// <param name="Mrz">Null for a document with no machine-readable zone, and for one never read.</param>
public sealed record KycIdentityDocumentDto(
    Guid KycFileId,
    string DocType,
    string? MaskedNumber,
    string? IssuingCountry,
    DateOnly? ExpiryDate,
    bool IsExpired,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, double> FieldConfidences,
    KycMrzDto? Mrz,
    string? StorageRef,
    string? ServiceVersion,
    DateTimeOffset ReadAt);

/// <param name="RawLine">
/// Always null, and declared so no caller waits for it. The raw MRZ line is never stored, by
/// design: that line spells the document number out in full, so persisting it in a plain jsonb
/// column would undo the encryption of the number three fields away. The parsed fields are kept
/// instead — minus the number, like the OCR fields.
/// </param>
/// <param name="ChecksumValid">
/// The zone's own check digits. False is a reading, not a refusal: whether it blocks the file is a
/// compliance decision taken on the whole verification.
/// </param>
public sealed record KycMrzDto(
    string? RawLine,
    bool ChecksumValid,
    IReadOnlyDictionary<string, string> Fields);
