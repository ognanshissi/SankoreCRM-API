namespace Sankore.Modules.Kyc.Features.Verification.GetVerification;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// What the verification panel of a file shows after the fact. A query — no <c>ICommand</c>, so
/// neither the transaction nor the audit behaviour runs.
///
/// <para>
/// It exists because <c>POST /verify</c> answers ONCE, synchronously, and a screen reopened an hour
/// later had nothing to read: the score, the flags and the face comparison were persisted and
/// unreachable. This is the read side of that write, and nothing more — re-running a verification
/// stays a command.
/// </para>
/// </summary>
internal sealed record GetKycVerificationQuery(Guid KycFileId)
    : IRequest<Result<KycVerificationDto>>;

/// <param name="Breakdown">
/// The per-criterion contributions **as the biometry service named them**, not projected onto a
/// fixed set of criteria. The service is external to this solution, so its criteria are its own;
/// mapping them to a closed list here would quietly drop the ones we had not foreseen.
/// </param>
/// <param name="BreakdownMaximum">
/// Always null, and declared so no caller invents one. The service reports a contribution per
/// criterion and no denominator, so a per-criterion gauge cannot be drawn faithfully — only the
/// global score is out of 100. Filling this in needs the service to report its maxima.
/// </param>
/// <param name="Flags">Service-raised warnings, verbatim: "EXPIRED_DOCUMENT", "NAME_MISMATCH"…</param>
/// <param name="Face">Null when no face comparison has ever run on this file.</param>
public sealed record KycVerificationDto(
    Guid KycFileId,
    string FileStatus,
    int? ConfidenceScore,
    string? ConfidenceLevel,
    DateTimeOffset? AssessedAt,
    string? Trigger,
    string? ScoreServiceVersion,
    IReadOnlyDictionary<string, int> Breakdown,
    int? BreakdownMaximum,
    IReadOnlyList<string> Flags,
    KycFaceComparisonDto? Face);

/// <param name="MatchPercent">
/// The similarity rounded to a whole percent. The service reports 0..1; a percentage is what the
/// screen shows, and rounding here keeps every caller from rounding differently.
/// </param>
/// <param name="IsMatch">
/// The service's own verdict against its own threshold. Never re-derived from
/// <paramref name="MatchPercent"/> — the threshold is the model's, not ours.
/// </param>
/// <param name="Attempt">
/// Which attempt this was, 1-based. It matters beyond display: reaching the tenant's
/// <c>face-match-max-attempts</c> adds the branch manager to the approval circuit.
/// </param>
public sealed record KycFaceComparisonDto(
    int MatchPercent,
    bool IsMatch,
    int Attempt,
    double? PortraitQuality,
    double? SelfieQuality,
    string? ModelVersion,
    DateTimeOffset ComparedAt);
