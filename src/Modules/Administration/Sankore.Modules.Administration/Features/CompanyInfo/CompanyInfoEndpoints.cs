using Sankore.Shared.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.CompanyInfo.CompanyLogo;
using Sankore.Modules.Administration.Features.CompanyInfo.GetCompanyInfo;
using Sankore.Modules.Administration.Features.CompanyInfo.PricingRules;
using Sankore.Modules.Administration.Features.CompanyInfo.QuoteIdentity;
using Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

namespace Sankore.Modules.Administration.Features.CompanyInfo;

internal static class CompanyInfoEndpoints
{
    public static IEndpointRouteBuilder MapCompanyInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("company-info").WithTags("Company Info").LiveModeWritesOnly();

        group.MapGetCompanyInfo();
        group.MapUpdateCompanyInfo();
        group.MapCompanyLogo();

        // VAT rates and discount ceiling (quote lines, lot 1)
        group.MapPricingRules();

        // Legal identity and quote settings (quote lines, lot 2)
        group.MapQuoteIdentity();

        return app;
    }
}