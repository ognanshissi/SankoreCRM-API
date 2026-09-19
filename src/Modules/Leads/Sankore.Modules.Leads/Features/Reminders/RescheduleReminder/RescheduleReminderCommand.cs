namespace Sankore.Modules.Leads.Features.Reminders.RescheduleReminder;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record RescheduleReminderCommand(
    Guid LeadId,
    Guid ReminderId,
    DateTimeOffset NewDueAt
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
