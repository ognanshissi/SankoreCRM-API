namespace Sankore.Modules.Leads.Features.Import.ValidateImport;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Import.Readers;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ValidateImportHandler(
    LeadsDbContext db,
    FileImportReader reader,
    IPhoneBlindIndexer phoneBlindIndexer)
    : IRequestHandler<ValidateImportCommand, Result<ValidateLeadImportResponse>>
{
    public async Task<Result<ValidateLeadImportResponse>> Handle(
        ValidateImportCommand cmd, CancellationToken ct)
    {
        var rows = await reader.ReadAsync(cmd.FileReference, ct);

        if (rows.Count == 0)
            return Result.Ok(new ValidateLeadImportResponse(0, 0, 0, 0, []));

        var validator = new LeadImportValidator(db, phoneBlindIndexer);

        return Result.Ok(await validator.ValidateAsync(
            rows, cmd.Defaults, LeadSource.FileImport, ct));
    }
}
