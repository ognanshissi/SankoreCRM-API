namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

public enum TimelineEventKind
{
    Activity,
    ScoreChange,
    Assignment,
    Reminder,
    Merge,
    DuplicateDismissed,
    Consent,
    Qualification
}

public sealed record TimelineEvent(
    DateTimeOffset OccurredAt,
    TimelineEventKind Kind,
    string Title,
    string? Detail,
    Guid? ActorId);
