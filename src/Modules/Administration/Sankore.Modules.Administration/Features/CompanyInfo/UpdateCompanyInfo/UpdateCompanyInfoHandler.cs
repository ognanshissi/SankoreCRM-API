using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

internal sealed class UpdateCompanyInfoHandler(
    AdministrationDbContext db)
    : IRequestHandler<UpdateCompanyInfoCommand, Result>
{
    public async Task<Result> Handle(UpdateCompanyInfoCommand request, CancellationToken ct)
    {
        // AsTracking: this context reads WITHOUT tracking by default, and the update used to answer
        // 204 while saving nothing — name, colours, language, currency (found 2026-11-10: the
        // theme « came back » to the default colour at every reload).
        var companyInfo = await db.CompanyInfos
            .AsTracking()
            .FirstOrDefaultAsync(ct);

        if (companyInfo is null)
            return Result.Fail("Company info not found for this tenant.");

        // The screen sends null for an empty field. Description and logo address are NOT NULL
        // columns: empty means empty. An empty colour means « not chosen »: the current one stays.
        companyInfo.Update(
            request.Name,
            request.Description ?? string.Empty,
            request.LogoUrl ?? string.Empty,
            string.IsNullOrWhiteSpace(request.PrimaryColor) ? companyInfo.PrimaryColor : request.PrimaryColor.Trim(),
            string.IsNullOrWhiteSpace(request.SecondaryColor) ? companyInfo.SecondaryColor : request.SecondaryColor.Trim(),
            request.DefaultLanguage,
            request.Currency);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
