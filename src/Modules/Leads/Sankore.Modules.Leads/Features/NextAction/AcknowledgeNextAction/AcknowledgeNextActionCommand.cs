namespace Sankore.Modules.Leads.Features.NextAction.AcknowledgeNextAction;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public enum NextActionAcknowledgement
{
    /// <summary>Accept the suggestion — creates a reminder at the suggested due date.</summary>
    Accept,
    /// <summary>Reschedule to a custom date — creates a reminder at <see cref="AcknowledgeNextActionCommand.RescheduledAt"/>.</summary>
    Reschedule,
    /// <summary>Ignore the suggestion — no reminder is created.</summary>
    Ignore
}

internal sealed record AcknowledgeNextActionCommand(
    Guid LeadId,
    NextActionAcknowledgement Action,
    DateTimeOffset? RescheduledAt = null
) : IRequest<Result<AcknowledgeNextActionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

internal sealed record AcknowledgeNextActionResult(
    Guid LeadId,
    NextActionAcknowledgement Action,
    Guid? ReminderId);
