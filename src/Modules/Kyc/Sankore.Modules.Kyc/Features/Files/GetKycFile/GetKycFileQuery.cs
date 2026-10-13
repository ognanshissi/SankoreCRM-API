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
    DateTimeOffset UpdatedAt,
    Guid? WorkflowInstanceId = null,
    string? WorkflowStatus = null,

    // ── Remote collection (KRM-B-08, read by KRM-F-08) ──────────────────────
    // Appended with defaults so the existing positional call sites keep compiling, like
    // ProductSummary.Id in M12.

    /// <summary>
    /// The product line the file was opened for, or null — a counter file is about a customer,
    /// not about a product. The screen resolves its name through M12's catalogue.
    /// </summary>
    Guid? ProductLineId = null,

    /// <summary>
    /// The agent accountable for this file reaching a decision. An ADDRESS, never a four-eyes
    /// anchor — see <c>KycFile.ResponsibleAgentId</c>.
    /// </summary>
    Guid? ResponsibleAgentId = null,

    /// <summary>
    /// <c>Manual</c>, <c>Automatic</c>, or null when this file never went through the remote
    /// decision policy — which is most of them, and is NOT the same statement as "a human
    /// decides it".
    /// </summary>
    string? ValidationRoute = null,

    /// <summary>
    /// A code from <c>KycRoutingReasons</c> when a human was asked, null on an automatic
    /// approval. The front end translates it (KRM-F-08); it is never shown to a customer.
    /// </summary>
    string? RoutingReason = null,

    DateTimeOffset? RoutedAt = null,

    /// <summary>
    /// When the customer accepted the processing of their biometric data, and against which
    /// version of the text (KRM-F-08 shows both). Null on a file nobody collected remotely.
    /// </summary>
    DateTimeOffset? ConsentGivenAt = null,

    string? ConsentTextVersion = null,

    /// <summary>
    /// Document kinds the customer photographed more times than the tenant's budget allows
    /// (KRM-B-06's « au-delà, envoi possible quand même et flag pour l'agent »). Empty is the
    /// ordinary case; a non-empty list is the flag, and KRM-F-08 shows it to the validator.
    /// </summary>
    IReadOnlyList<string>? ExcessiveQualityRetries = null);
