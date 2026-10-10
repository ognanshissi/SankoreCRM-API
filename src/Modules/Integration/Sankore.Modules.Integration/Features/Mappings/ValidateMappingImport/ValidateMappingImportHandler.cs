namespace Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;

using MediatR;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ValidateMappingImportHandler(
    IntegrationDbContext db,
    MappingImportReader reader,
    MappingImportValidator validator,
    IFileStore fileStore)
    : IRequestHandler<ValidateMappingImportCommand, Result<MappingImportReport>>
{
    public async Task<Result<MappingImportReport>> Handle(
        ValidateMappingImportCommand cmd, CancellationToken ct)
    {
        try
        {
            if (!await db.ConnectionExistsAsync(cmd.ConnectionId, ct))
                return Result.Fail<MappingImportReport>(IntegrationErrors.ConnectionNotFound);

            var file = await reader.ReadAsync(cmd.FileReference, ct);

            if (file.HasHeaderError)
                return Result.Fail<MappingImportReport>(file.HeaderError!);

            // The same validator the import runs, with no database read of its own, which is the
            // only reason the two reports can be trusted to agree.
            return Result.Ok(validator.Validate(file.Rows));
        }
        finally
        {
            // A dry run leaves nothing behind — it is not an import, so there is no reason to
            // keep a copy of the file.
            if (cmd.DeleteAfterwards)
                await fileStore.DeleteAsync(cmd.FileReference, CancellationToken.None);
        }
    }
}
