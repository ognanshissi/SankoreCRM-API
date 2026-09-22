namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

using MediatR;
using Sankore.Modules.Administration.Features.ImportUsers.Readers;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ValidateImportHandler(
    AdministrationDbContext db,
    FileImportReader reader)
    : IRequestHandler<ValidateImportCommand, Result<ValidateImportResponse>>
{
    public async Task<Result<ValidateImportResponse>> Handle(
        ValidateImportCommand cmd, CancellationToken ct)
    {
        var rows = await reader.ReadAsync(cmd.FileReference, ct);

        if (rows.Count == 0)
            return Result.Ok(new ValidateImportResponse(0, 0, 0, []));

        var validator = new ImportValidator(db);
        return Result.Ok(await validator.ValidateAsync(rows, cmd.TenantId, ct));
    }
}
