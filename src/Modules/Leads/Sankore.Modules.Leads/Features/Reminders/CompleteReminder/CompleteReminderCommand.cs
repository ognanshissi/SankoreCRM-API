namespace Sankore.Modules.Leads.Features.Reminders.CompleteReminder;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CompleteReminderCommand(Guid LeadId, Guid ReminderId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadReminder";
    public string? ResourceId  => ReminderId.ToString();
}
