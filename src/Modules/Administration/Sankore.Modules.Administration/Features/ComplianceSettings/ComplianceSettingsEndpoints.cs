namespace Sankore.Modules.Administration.Features.ComplianceSettings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.ComplianceSettings.GetComplianceSettings;
using Sankore.Modules.Administration.Features.ComplianceSettings.UpdateComplianceSettings;

internal static class ComplianceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapComplianceSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("compliance-settings").WithTags("Compliance Settings");

        group.MapGetComplianceSettings();
        group.MapUpdateComplianceSettings();

        return app;
    }
}
