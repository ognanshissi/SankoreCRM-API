namespace Sankore.Modules.Leads.Features.Reminders.CreateReminder;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateReminderCommand(
    Guid LeadId,
    string Title,
    Guid CreatedBy,
    DateTimeOffset DueAt,
    string? Notes = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId  => LeadId.ToString();
}
