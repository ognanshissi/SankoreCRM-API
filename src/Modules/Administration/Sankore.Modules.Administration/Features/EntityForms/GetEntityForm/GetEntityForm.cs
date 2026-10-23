namespace Sankore.Modules.Administration.Features.EntityForms.GetEntityForm;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain.Forms;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal sealed record GetEntityFormQuery(FormEntity Entity) : IRequest<Result<EntityFormDto>>;

internal sealed class GetEntityFormHandler(AdministrationDbContext db, ITenantContext tenant, ITenantModules modules)
    : IRequestHandler<GetEntityFormQuery, Result<EntityFormDto>>
{
    private const string CategoryField = "category";

    public async Task<Result<EntityFormDto>> Handle(GetEntityFormQuery query, CancellationToken ct)
    {
        var layout = await db.EntityFormLayouts.FirstOrDefaultAsync(f => f.Entity == query.Entity, ct);
        var form = EntityFormResolver.Resolve(query.Entity, layout?.Version ?? 0, layout?.Layout);

        return Result.Ok(query.Entity == FormEntity.Product ? await OfferedCategoriesOnlyAsync(form, ct) : form);
    }

    /// <summary>
    /// The product form offers Standard and the categories of the modules in force: this list is
    /// what the creation drawer shows, and CreateProduct refuses the others anyway. Filtered here
    /// and not in <see cref="EntityFormResolver"/>, which <c>EntityFormRules</c> validates layouts
    /// against — a saved condition « category = Tontine » must stay valid while the module is off.
    /// </summary>
    private async Task<EntityFormDto> OfferedCategoriesOnlyAsync(EntityFormDto form, CancellationToken ct)
    {
        var active = await modules.ActiveAsync(tenant.CurrentTenantId, ct);
        var fields = form.Fields
            .Select(f => f.Key == CategoryField && f.Options is { } options
                ? f with
                {
                    Options = options
                        .Where(o => !Enum.TryParse<ProductCategory>(o.Value, out var category)
                                    || ProductCategories.IsOpenWith(category, active))
                        .ToList(),
                }
                : f)
            .ToList();
        return form with { Fields = fields };
    }
}

internal static class GetEntityFormEndpoint
{
    internal static IEndpointRouteBuilder MapGetEntityForm(this IEndpointRouteBuilder app)
    {
        // No permission beyond being signed in: whoever creates a lead, a client or a product
        // renders its form, and the layout reveals nothing the screen would not show.
        app.MapGet("{entity}", Handle)
            .WithName("GetEntityForm")
            .WithSummary("The tenant's form for a lead, a client or a product: sections, native and custom fields")
            .RequireAuthorization()
            .Produces<EntityFormDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(FormEntity entity, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetEntityFormQuery(entity), ct);
        return Results.Ok(result.Value);
    }
}
