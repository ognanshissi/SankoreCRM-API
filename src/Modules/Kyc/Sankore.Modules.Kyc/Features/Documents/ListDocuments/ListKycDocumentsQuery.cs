namespace Sankore.Modules.Kyc.Features.Documents.ListDocuments;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Every image attached to a file, with the verdict a validator gave it. The list the module never
/// had — until the registry existed there was nothing to enumerate.
/// </summary>
internal sealed record ListKycDocumentsQuery(Guid KycFileId)
    : IRequest<Result<KycDocumentListDto>>;

/// <param name="AllCurrentDocumentsAccepted">
/// True when every CURRENT document — the newest of each kind — has been accepted, and there is at
/// least one. It is a report, not a trigger: accepting the last outstanding document deliberately
/// does not advance the file, so this is what a screen uses to tell the validator they can now take
/// the file-level decision.
///
/// <para>
/// False for a file whose documents predate per-document review, which is why
/// <paramref name="AnyCurrentDocumentNotReviewed"/> exists beside it: "not all accepted" and
/// "nobody was ever asked" must not look the same.
/// </para>
/// </param>
/// <param name="AnyCurrentDocumentNotReviewed">
/// True when at least one current document predates the registry. Such a file was decided as a
/// whole by the approval ladder, and no per-document verdict can be recovered for it.
/// </param>
public sealed record KycDocumentListDto(
    Guid KycFileId,
    string FileStatus,
    bool AllCurrentDocumentsAccepted,
    bool AnyCurrentDocumentNotReviewed,
    IReadOnlyList<KycDocumentDto> Documents);

/// <param name="Id">
/// Null for a document that predates the registry — there is no row to address, so such an image
/// cannot be reviewed. A screen must render it as evidence, not as something awaiting a decision.
/// </param>
/// <param name="StorageRef">
/// Opaque, and what <c>GET kyc-files/{id}/documents/{storageRef}</c> takes. Exposed under
/// <c>kyc:read</c> like <c>KycIdentityDocumentDto.StorageRef</c> already is: the ref is not the
/// image, fetching the bytes needs <c>kyc:document:reveal</c>, and that endpoint is rate-limited
/// precisely because a script walking refs is its threat model.
/// </param>
/// <param name="ContentType">
/// Null for a pre-registry document: the content type, the size and the digest were computed at
/// upload and persisted nowhere, so no backfill can recover them.
/// </param>
/// <param name="IsCurrentForKind">
/// Whether this is the newest upload of its kind — the one the file is actually judged on. Only a
/// current document can be reviewed.
/// </param>
/// <param name="HasOcrReading">
/// Whether a verification has read this image. Resolved by matching the storage ref against
/// <c>kyc_identity_documents</c>, so it needs no column of its own.
/// </param>
public sealed record KycDocumentDto(
    Guid? Id,
    string Kind,
    string StorageRef,
    string? ContentType,
    long? SizeBytes,
    string? Sha256,
    Guid? UploadedBy,
    DateTimeOffset UploadedAt,
    string ReviewDecision,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? RefusalReason,
    bool IsCurrentForKind,
    bool HasOcrReading);
