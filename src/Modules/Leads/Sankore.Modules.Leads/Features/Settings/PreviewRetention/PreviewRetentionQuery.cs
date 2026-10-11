namespace Sankore.Modules.Leads.Features.Settings.PreviewRetention;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// What tonight's anonymisation run would destroy for the caller's tenant (BNF-B-19).
///
/// <para>
/// A query, and the only safety check that exists for an IRREVERSIBLE job: the default retention
/// is 24 months, the job runs at 03:45, and the first run against a database that has been
/// collecting leads for longer destroys every lead closed before the cutoff — with no way back.
/// Before this endpoint the only way to know the figure was a SQL session, which is also the only
/// way <c>lead-retention-months</c> could be read at all.
/// </para>
/// </summary>
internal sealed record PreviewRetentionQuery : IRequest<Result<RetentionPreviewDto>>;

/// <summary>
/// <param name="RetentionMonths">The period in force for this tenant.</param>
/// <param name="Cutoff">
/// A lead closed before this instant is due for anonymisation. Computed by the same
/// <c>RetentionSelection.CutoffFor</c> the job uses.
/// </param>
/// <param name="DueNow">
/// How many leads the selection matches right now. The job destroys at most
/// <paramref name="MaxPerRun"/> of them per night, so a larger backlog is worked through over
/// several nights.
/// </param>
/// <param name="MaxPerRun">
/// The ceiling one run honours. Exposed because "due now" being far larger is not a malfunction,
/// and an administrator watching the figure fall needs to know why it falls in steps.
/// </param>
/// <param name="OldestClosedAt">
/// When the oldest due lead was closed, or null when nothing is due. It is the figure that tells
/// an administrator whether the period is about to reach into history nobody expected.
/// </param>
/// <param name="IrreversibleWarning">
/// Always populated. The response is read by somebody about to raise or lower a retention period,
/// and the destruction cannot be undone.
/// </param>
/// </summary>
public sealed record RetentionPreviewDto(
    int RetentionMonths,
    DateTimeOffset Cutoff,
    int DueNow,
    int MaxPerRun,
    DateTimeOffset? OldestClosedAt,
    string IrreversibleWarning);
