namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

public enum TimelineEventKind
{
    Activity,
    ScoreChange,
    Assignment,
    Reminder,
    Merge,
    DuplicateDismissed
}

public sealed record TimelineEvent(
    DateTimeOffset OccurredAt,
    TimelineEventKind Kind,
    string Title,
    string? Detail,
    Guid? ActorId);
