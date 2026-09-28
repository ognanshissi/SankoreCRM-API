namespace Sankore.Modules.Customers.Features.Compliance;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Export.DownloadClientExport;
using Sankore.Modules.Customers.Features.Compliance.Export.GetClientExport;
using Sankore.Modules.Customers.Features.Compliance.Export.RequestClientExport;
using Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;
using Sankore.Modules.Customers.Features.Compliance.Retention.ListRetentionCandidates;
using Sankore.Modules.Customers.Features.Compliance.Settings.GetCustomerSetting;
using Sankore.Modules.Customers.Features.Compliance.Settings.ListCustomerSettings;
using Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;

/// <summary>
/// Zone aggregator (US-M01-BE-01 / 29 / 30). Two route groups rather than one, because the zone
/// spans two resources: the tenant configuration under <c>customer-settings</c> and the
/// compliance operations on clients under <c>clients</c>.
/// <para>
/// Sharing the <c>clients</c> prefix with the Clients zone is intentional and safe: literal
/// segments win over route parameters in ASP.NET Core routing, so <c>clients/exports</c> and
/// <c>clients/retention/candidates</c> are matched before <c>clients/{clientId}</c>.
/// </para>
/// </summary>
public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("customer-settings").WithTags("Customer Settings");

        settings
            .MapListCustomerSettings()
            .MapGetCustomerSetting()
            .MapUpdateCustomerSetting();

        var compliance = app.MapGroup("clients").WithTags("Customer Compliance");

        compliance
            .MapListRetentionCandidates()
            .MapAnonymizeClient()
            .MapRequestClientExport()
            .MapGetClientExport()
            .MapDownloadClientExport();

        return app;
    }
}
