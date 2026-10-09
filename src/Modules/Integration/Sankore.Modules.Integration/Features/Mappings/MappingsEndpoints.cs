namespace Sankore.Modules.Integration.Features.Mappings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Mappings.DeleteMapping;
using Sankore.Modules.Integration.Features.Mappings.ExportMappings;
using Sankore.Modules.Integration.Features.Mappings.ImportMappings;
using Sankore.Modules.Integration.Features.Mappings.ListMappings;
using Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;
using Sankore.Modules.Integration.Features.Mappings.UpsertMapping;
using Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;

/// <summary>
/// Aggregator of the mappings area (INT-04). Adding a slice touches its own folder plus exactly
/// one line here.
///
/// <para>
/// The connection is in the URL because the mapping belongs to it, not to the tenant: an IMF
/// connected to a CBS and to two insurers has three different vocabularies for the same CRM
/// product, and a tenant-wide route would make the last one written win.
/// </para>
/// </summary>
public static class MappingsEndpoints
{
    public static IEndpointRouteBuilder MapMappingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app
            .MapGroup("integration/connections/{connectionId:guid}/mappings")
            .WithTags("Integration");

        // The literal sub-routes ("{domain}/import", "{domain}/unmapped", "{domain}/export.csv")
        // and the parameter route ("{domain}/{crmCode}") never collide: the parameter route is
        // PUT and DELETE only, the literals are GET and POST.
        return group
            .MapListMappings()
            .MapUpsertMapping()
            .MapDeleteMapping()
            .MapImportMappings()
            .MapValidateMappingImport()
            .MapExportMappings()
            .MapListUnmappedCodes();
    }
}
