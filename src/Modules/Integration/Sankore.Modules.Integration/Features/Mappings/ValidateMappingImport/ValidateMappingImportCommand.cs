namespace Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The dry run: the same report as the import, writing nothing.
///
/// <para>
/// Still an <c>ICommand</c> and an <c>IResourceCommand</c> even though it mutates nothing — the
/// precedent is M01's <c>ValidateClientImportCommand</c>. It pulls a whole file of a tenant's
/// configuration into the system, so the audit trail must record who did it; and the audit
/// behaviour only fires for <c>ICommand</c>.
/// </para>
/// </summary>
internal sealed record ValidateMappingImportCommand(
    Guid ConnectionId,
    MappingDomain Domain,
    string FileReference,
    bool DeleteAfterwards)
    : IRequest<Result<MappingImportReport>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationMapping";

    public string? ResourceId => ConnectionId.ToString();
}
