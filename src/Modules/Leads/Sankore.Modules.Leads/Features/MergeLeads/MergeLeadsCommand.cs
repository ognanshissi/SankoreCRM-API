namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public sealed record MergeLeadsCommand(
    Guid TargetLeadId,
    Guid SourceLeadId,
    Guid MergedBy,
    /// <summary>
    /// Declares which optional scalar fields should be taken from the source lead.
    /// When null, all target fields are preserved as-is.
    /// </summary>
    MergeFieldPreferences? FieldPreferences = null
) : IRequest<Result<MergeLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => TargetLeadId.ToString();
}

public sealed record MergeLeadResult(
    Guid MergeId,
    Guid TargetLeadId,
    Guid SourceLeadId,
    DateTimeOffset MergedAt,
    IReadOnlyList<string> OverriddenFields);
