namespace Sankore.Modules.Integration.Features.Mappings.ImportMappings;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Imports a whole domain's correspondence table from a CSV file (INT-04, criterion 2).
///
/// <para>
/// Synchronous, unlike M01's client import: a mapping table is a few hundred lines of three
/// columns and the operator is waiting for the rejected-line report. There is no background job
/// to poll and no partial state to resume.
/// </para>
/// </summary>
internal sealed record ImportMappingsCommand(
    Guid ConnectionId,
    MappingDomain Domain,
    string FileReference,
    bool DeleteAfterwards)
    : IRequest<Result<MappingImportReport>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationMapping";

    public string? ResourceId => ConnectionId.ToString();
}
