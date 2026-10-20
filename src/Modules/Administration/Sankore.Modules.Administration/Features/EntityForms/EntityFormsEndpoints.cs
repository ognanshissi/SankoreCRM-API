namespace Sankore.Modules.Administration.Features.EntityForms;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.EntityForms.GetEntityForm;
using Sankore.Modules.Administration.Features.EntityForms.SaveEntityForm;

/// <summary>Dynamic forms of the lead, client and product entities — <c>forms/{Lead|Client|Product}</c>.</summary>
internal static class EntityFormsEndpoints
{
    internal static IEndpointRouteBuilder MapEntityFormsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("forms").WithTags("Forms");
        group.MapGetEntityForm();
        group.MapSaveEntityForm();
        return app;
    }
}
