namespace Sankore.Modules.Customers.Features.Import.ValidateImport;

using MediatR;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Shared.Kernel;

internal sealed class ValidateClientImportHandler(
    ClientFileImportReader reader,
    ClientImportValidator validator,
    IFileStore fileStore)
    : IRequestHandler<ValidateClientImportCommand, Result<ValidateClientImportResponse>>
{
    public async Task<Result<ValidateClientImportResponse>> Handle(
        ValidateClientImportCommand cmd, CancellationToken ct)
    {
        try
        {
            var rows = await reader.ReadAsync(cmd.FileReference, ct);
            var response = await validator.ValidateAsync(rows, cmd.TenantId, ct);
            return Result.Ok(response);
        }
        finally
        {
            // A dry run leaves nothing behind: the uploaded file is not an import, so keeping
            // a copy of a few hundred people's identity documents would be gratuitous.
            if (cmd.DeleteAfterwards)
                await fileStore.DeleteAsync(cmd.FileReference, CancellationToken.None);
        }
    }
}
