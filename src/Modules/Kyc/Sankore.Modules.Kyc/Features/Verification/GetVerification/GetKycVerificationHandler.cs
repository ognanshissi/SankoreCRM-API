namespace Sankore.Modules.Kyc.Features.Verification.GetVerification;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the LATEST assessment and the LATEST face comparison of a file.
///
/// <para>
/// Latest, not all of them: a file is re-scored after every field correction, and a panel showing
/// the history of its own scores invites an agent to argue with a superseded one. The rows are kept
/// — they are evidence, and <c>KycConfidenceAssessment.Trigger</c> says what caused each — but this
/// endpoint answers "where does the file stand", which has one answer.
/// </para>
/// </summary>
internal sealed class GetKycVerificationHandler(
    KycDbContext db,
    ILogger<GetKycVerificationHandler> logger)
    : IRequestHandler<GetKycVerificationQuery, Result<KycVerificationDto>>
{
    public async Task<Result<KycVerificationDto>> Handle(
        GetKycVerificationQuery query, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant: a file of another tenant is
        // simply absent, and the endpoint answers 404 rather than 403.
        var file = await db.KycFiles
            .Where(f => f.Id == query.KycFileId)
            .Select(f => new { f.Id, f.Status })
            .FirstOrDefaultAsync(ct);

        if (file is null)
            return Result.Fail<KycVerificationDto>(KycErrors.FileNotFound);

        var assessment = await db.KycConfidenceAssessments
            .Where(a => a.KycFileId == query.KycFileId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Ordered by Attempt, not CreatedAt: the attempt number is the unique, monotonic sequence
        // on this file, and two attempts recorded in the same clock tick would otherwise be
        // ordered arbitrarily.
        var face = await db.KycFaceVerifications
            .Where(v => v.KycFileId == query.KycFileId)
            .OrderByDescending(v => v.Attempt)
            .FirstOrDefaultAsync(ct);

        return Result.Ok(new KycVerificationDto(
            KycFileId: file.Id,
            FileStatus: file.Status.ToString(),
            ConfidenceScore: assessment?.GlobalScore,
            ConfidenceLevel: assessment?.Level.ToString(),
            AssessedAt: assessment?.CreatedAt,
            Trigger: assessment?.Trigger,
            ScoreServiceVersion: assessment?.ServiceVersion,
            Breakdown: Deserialize<Dictionary<string, int>>(assessment?.BreakdownJson, nameof(assessment.BreakdownJson))
                       ?? [],
            // See KycVerificationDto: there is no per-criterion maximum to report.
            BreakdownMaximum: null,
            Flags: Deserialize<List<string>>(assessment?.FlagsJson, nameof(assessment.FlagsJson)) ?? [],
            Face: ToFaceDto(face)));
    }

    private KycFaceComparisonDto? ToFaceDto(KycFaceVerification? face)
    {
        if (face is null) return null;

        var quality = Deserialize<Dictionary<string, double>>(
            face.QualityScoresJson, nameof(face.QualityScoresJson));

        return new KycFaceComparisonDto(
            // Rounded away from zero so 0.495 reads as 50%, matching what the service's own
            // threshold comparison would have seen rather than banker's rounding to 49.
            MatchPercent: (int)Math.Round(face.SimilarityScore * 100, MidpointRounding.AwayFromZero),
            IsMatch: face.IsMatch,
            Attempt: face.Attempt,
            PortraitQuality: Lookup(quality, "portrait"),
            SelfieQuality: Lookup(quality, "selfie"),
            ModelVersion: face.ModelVersion,
            ComparedAt: face.CreatedAt);
    }

    private static double? Lookup(Dictionary<string, double>? values, string key)
        => values is not null && values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// A malformed blob yields null and a warning, never a 500. These columns hold whatever an
    /// external service sent on the day it was called, and a panel that cannot render one bad row
    /// is worse than a panel missing one section — the rest of the verification is still true.
    /// </summary>
    private T? Deserialize<T>(string? json, string column) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not read {Column} of a KYC assessment; the section is omitted", column);
            return null;
        }
    }
}
