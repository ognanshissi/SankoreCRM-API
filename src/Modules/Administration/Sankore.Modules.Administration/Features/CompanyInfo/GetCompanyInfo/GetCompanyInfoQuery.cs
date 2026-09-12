using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.CompanyInfo.GetCompanyInfo;

public record GetCompanyInfoQuery(Guid TenantId) : IRequest<CompanyInfoDto?>;

public record CompanyInfoDto(
    string Name,
    string Description,
    string LogoUrl,
    string PrimaryColor,
    string SecondaryColor,
    string DefaultLanguage);

internal sealed class GetCompanyInfoHandler(AdministrationDbContext db)
    : IRequestHandler<GetCompanyInfoQuery, CompanyInfoDto?>
{
    public async Task<CompanyInfoDto?> Handle(GetCompanyInfoQuery request, CancellationToken cancellationToken)
    {
        var info = await db.CompanyInfos
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == request.TenantId)
            .Select(c => new CompanyInfoDto(
                c.Name,
                c.Description,
                c.LogoUrl,
                c.PrimaryColor,
                c.SecondaryColor,
                c.DefaultLanguage.ToString()))
            .FirstOrDefaultAsync(cancellationToken);

        return info;
    }
}