namespace Sankore.Modules.Leads.Features.Reminders.ListReminders;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListRemindersQuery(
    Guid LeadId,
    ReminderStatus? Status = null
) : IRequest<Result<IReadOnlyList<ReminderDto>>>;

public sealed record ReminderDto(
    Guid Id,
    string Title,
    string? Notes,
    DateTimeOffset DueAt,
    Guid CreatedBy,
    ReminderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    bool IsOverdue);
