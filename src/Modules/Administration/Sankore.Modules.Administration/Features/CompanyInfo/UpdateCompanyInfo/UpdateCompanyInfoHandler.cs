using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

internal sealed class UpdateCompanyInfoHandler(
    AdministrationDbContext db,
    ITenantContext tenantContext)
    : IRequestHandler<UpdateCompanyInfoCommand, Result>
{
    public async Task<Result> Handle(UpdateCompanyInfoCommand request, CancellationToken ct)
    {
        var companyInfo = await db.CompanyInfos
            .FirstOrDefaultAsync(ct);

        if (companyInfo is null)
            return Result.Fail("Company info not found for this tenant.");

        companyInfo.Update(
            request.Name,
            request.Description,
            request.LogoUrl,
            request.PrimaryColor,
            request.SecondaryColor,
            request.DefaultLanguage);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
