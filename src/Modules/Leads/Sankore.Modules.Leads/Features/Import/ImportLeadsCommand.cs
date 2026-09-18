namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public sealed record ImportLeadsCommand(
    Guid TenantId,
    IReadOnlyList<ImportLeadRow> Rows
) : IRequest<Result<LeadImportResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => null;
}
