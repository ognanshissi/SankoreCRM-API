namespace Sankore.Modules.Kyc.Features.Reviews.RaiseReview;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Raises a KYC review by hand, without waiting for the periodic deadline — a transaction alert,
/// a new identity document, a change of beneficial owner, a press mention.
///
/// <para>
/// The review is scheduled for TODAY: the point of an event-driven review is that it is owed now.
/// The daily sweep is what then marks it due, moves the file to UnderReview and notifies the
/// agency, so a periodic review and an event-driven one are announced by exactly one code path.
/// </para>
///
/// <para>
/// <see cref="Reason"/> is mandatory: an unexplained review is noise to the officer who picks it
/// up. It is stored in clear and shown to an operator, so it must NEVER carry a sensitive value —
/// no document number, no phone, no address, no amount tied to a named person. The UI must label
/// the field as a justification for exactly that reason.
/// </para>
/// </summary>
internal sealed record RaiseKycReviewCommand(
    Guid KycFileId,
    string Reason,
    Guid RaisedBy
) : IRequest<Result<RaiseKycReviewResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycFile";
    public string? ResourceId => KycFileId.ToString();
}

internal sealed record RaiseKycReviewResult(Guid ReviewId, DateOnly DueDate);

internal static class ReviewErrors
{
    /// <summary>
    /// Lives here rather than in <c>KycErrors</c>, which another task owns this round — the same
    /// arrangement <c>DuplicateErrors</c> made. Same stable UPPER_SNAKE contract; it moves into
    /// <c>KycErrors</c> on the next pass.
    /// </summary>
    public const string ReasonRequired = Domain.KycErrors.ReviewReasonRequired;

    /// <summary>A closed file has no next review to raise.</summary>
    public const string FileNotOpen = Domain.KycErrors.FileNotOpen;
}
