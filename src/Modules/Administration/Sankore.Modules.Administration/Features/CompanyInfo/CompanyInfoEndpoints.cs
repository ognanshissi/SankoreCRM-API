using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.CompanyInfo.GetCompanyInfo;
using Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

namespace Sankore.Modules.Administration.Features.CompanyInfo;

internal static class CompanyInfoEndpoints
{
    public static IEndpointRouteBuilder MapCompanyInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("company-info").WithTags("Company Info");

        group.MapGetCompanyInfo();
        group.MapUpdateCompanyInfo();

        return app;
    }
}