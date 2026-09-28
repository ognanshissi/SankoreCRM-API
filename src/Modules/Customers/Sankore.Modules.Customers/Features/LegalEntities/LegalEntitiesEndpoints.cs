namespace Sankore.Modules.Customers.Features.LegalEntities;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;
using Sankore.Modules.Customers.Features.LegalEntities.DeactivateLegalForm;
using Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;
using Sankore.Modules.Customers.Features.LegalEntities.ListBeneficialOwners;
using Sankore.Modules.Customers.Features.LegalEntities.ListLegalForms;

/// <summary>
/// Area aggregator for US-M01-BE-17 / 18. Two route groups, because the area spans two
/// resources: the legal-entity side of <c>clients</c> and the tenant-level
/// <c>legal-forms</c> reference list.
/// </summary>
public static class LegalEntitiesEndpoints
{
    public static IEndpointRouteBuilder MapLegalEntitiesEndpoints(this IEndpointRouteBuilder app)
    {
        // POST clients/legal, POST|GET clients/{clientId}/beneficial-owners
        var clients = app.MapGroup("clients").WithTags("LegalEntities");
        clients
            .MapCreateLegalClient()
            .MapDeclareBeneficialOwners()
            .MapListBeneficialOwners();

        // GET|POST legal-forms, DELETE legal-forms/{code}
        var legalForms = app.MapGroup("legal-forms").WithTags("LegalForms");
        legalForms
            .MapListLegalForms()
            .MapCreateLegalForm()
            .MapDeactivateLegalForm();

        return app;
    }
}
