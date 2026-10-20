namespace Sankore.Modules.Administration.Features.EntityForms.SaveEntityForm;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain.Forms;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal sealed record SaveEntityFormCommand(
    FormEntity Entity,
    int ExpectedVersion,
    IReadOnlyList<FormSection> Sections,
    IReadOnlyList<FormField> Fields
) : IRequest<Result<EntityFormDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "EntityForm";
    public string? ResourceId => Entity.ToString();
}

internal sealed class SaveEntityFormHandler(
    AdministrationDbContext db, ITenantContext tenant, ICurrentUser currentUser, TimeProvider clock)
    : IRequestHandler<SaveEntityFormCommand, Result<EntityFormDto>>
{
    public async Task<Result<EntityFormDto>> Handle(SaveEntityFormCommand cmd, CancellationToken ct)
    {
        var layout = await db.EntityFormLayouts
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Entity == cmd.Entity, ct);

        if (layout is null)
        {
            layout = EntityFormLayout.Create(tenant.CurrentTenantId, cmd.Entity);
            db.EntityFormLayouts.Add(layout);
        }

        var replaced = layout.Replace(
            new FormSectionsAndFields(cmd.Sections ?? [], cmd.Fields ?? []),
            cmd.ExpectedVersion, currentUser.Id, clock.GetUtcNow());

        if (replaced.IsFailure)
            return Result.Fail<EntityFormDto>(replaced.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first saves of the same form raced on the (tenant, entity) unique index: the one
            // that lost is told to reload, exactly like a version mismatch.
            return Result.Fail<EntityFormDto>("FORM_VERSION_CONFLICT");
        }

        return Result.Ok(EntityFormResolver.Resolve(cmd.Entity, layout.Version, layout.Layout));
    }
}

internal static class SaveEntityFormEndpoint
{
    internal static IEndpointRouteBuilder MapSaveEntityForm(this IEndpointRouteBuilder app)
    {
        app.MapPut("{entity}", Handle)
            .WithName("SaveEntityForm")
            .WithSummary("Replace the layout of a form: sections, native field placement and custom fields")
            .RequireAuthorization(Permissions.CanManageEntityForms.Code)
            .Produces<EntityFormDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        FormEntity entity, SaveEntityFormRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new SaveEntityFormCommand(entity, req.ExpectedVersion, req.Sections, req.Fields), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error == "FORM_VERSION_CONFLICT"
            ? Results.Problem(title: "Form changed meanwhile", detail: result.Error, statusCode: StatusCodes.Status409Conflict)
            : Results.Problem(title: "Invalid form layout", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

/// <param name="ExpectedVersion">The <c>version</c> of the form the editor loaded (0 for a form never saved).</param>
/// <param name="Fields">
/// Every field the editor shows. Native fields carry only placement, wording and flags — their type,
/// options and limits are taken from the server's catalogue whatever is sent. A custom field saved
/// before must still be present: archive it instead of removing it.
/// </param>
internal sealed record SaveEntityFormRequest(
    int ExpectedVersion,
    IReadOnlyList<FormSection> Sections,
    IReadOnlyList<FormField> Fields);
