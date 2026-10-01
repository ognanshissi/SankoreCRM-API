namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>By file id, or by the customer it belongs to — the screen has one or the other.</summary>
internal sealed record GetKycFileQuery(Guid? KycFileId, Guid? CustomerId) : IRequest<Result<KycFileDto>>;

/// <summary>
/// What a KYC screen needs. Carries no document number, no OCR field and no image reference:
/// those are sensitive, they are revealed through their own audited endpoint, and a list DTO gets
/// exported, logged and pasted into tickets.
/// </summary>
public sealed record KycFileDto(
    Guid Id,
    Guid CustomerId,
    string Status,
    string Tier,
    string Channel,
    string VigilanceLevel,
    int? ConfidenceScore,
    string? ConfidenceLevel,
    bool DuplicateSuspected,
    int FaceMatchAttempts,
    DateOnly? NextReviewDate,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
