namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ValidateImportCommand(
    Guid TenantId,
    string FileReference
) : IRequest<Result<ValidateImportResponse>>;
