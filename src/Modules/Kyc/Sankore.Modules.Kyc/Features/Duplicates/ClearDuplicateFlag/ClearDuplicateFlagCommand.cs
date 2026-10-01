namespace Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Lifts a duplicate suspicion the detector raised — twins on consecutive card numbers, a
/// re-enrolment after a lost card, a transcription the agent has since verified.
///
/// <para>
/// <see cref="Reason"/> is mandatory and is deliberately NOT marked sensitive: it is the whole
/// evidence that the lift was a decision and not a click, and the audit entry this command
/// produces is where a reviewer reads it. The UI must label it as a justification — it is stored
/// in the audit log, so it must never be used to carry a personal value.
/// </para>
/// </summary>
internal sealed record ClearDuplicateFlagCommand(
    Guid KycFileId,
    string Reason,
    Guid ClearedBy
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

internal static class DuplicateErrors
{
    /// <summary>
    /// Lives here rather than in <c>KycErrors</c>, which another task owns this round. Same
    /// UPPER_SNAKE contract; it moves there on the next pass.
    /// </summary>
    public const string ClearReasonRequired = Domain.KycErrors.DuplicateClearReasonRequired;
}
