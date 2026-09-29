namespace Sankore.Modules.Customers.Features.Import.ValidateImport;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Dry run: reads the file and reports what WOULD happen, creating nothing. Audited like a
/// mutation because it reads a whole file of personal data into the system.
/// </summary>
internal sealed record ValidateClientImportCommand(
    Guid TenantId,
    string FileReference,
    bool DeleteAfterwards
) : IRequest<Result<ValidateClientImportResponse>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientImport";
    public string? ResourceId => null;
}
