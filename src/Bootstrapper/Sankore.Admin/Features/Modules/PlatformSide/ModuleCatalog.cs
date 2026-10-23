using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Modules.PlatformSide;

public sealed record UpdateModuleDefinitionRequest(string Label, string Description, decimal MonthlyPrice,
    string Currency, bool IsAvailable);

/// <summary>
/// The offer: what each module is called, what it costs, whether it can be asked for. Read by
/// Sankore.Api (service key) and by the admin front-end; changed by a platform administrator only.
/// The two definitions are created by the migration — there is no route to add or remove one,
/// because a module is code before it is a row.
/// </summary>
public static class ModuleCatalogEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListModuleCatalogQuery(), ct)))
            .WithName("ListModuleCatalog")
            .WithSummary("The paid modules, their wording and current price")
            .Produces<List<ModuleDefinitionResponse>>()
            .WithOpenApi();

        group.MapPut("/{code}", async (string code, UpdateModuleDefinitionRequest body, ISender sender, CancellationToken ct) =>
                (await sender.Send(new UpdateModuleDefinitionCommand(code, body.Label, body.Description,
                    body.MonthlyPrice, body.Currency, body.IsAvailable), ct))
                .ToHttp(ModuleHttp.NoContent))
            .RequirePlatformAdmin()
            .WithName("UpdateModuleDefinition")
            .WithSummary("Change a module's wording, price or availability (platform administrators only)")
            .WithDescription("A new price applies to requests made afterwards. Pending and active "
                + "subscriptions keep the price their tenant accepted.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record ListModuleCatalogQuery : IRequest<List<ModuleDefinitionResponse>>;

internal sealed class ListModuleCatalogHandler(AdminDbContext db)
    : IRequestHandler<ListModuleCatalogQuery, List<ModuleDefinitionResponse>>
{
    public async Task<List<ModuleDefinitionResponse>> Handle(ListModuleCatalogQuery request, CancellationToken ct) =>
        (await db.ModuleDefinitions.AsNoTracking().OrderBy(m => m.Code).ToListAsync(ct))
        .Select(ModuleDefinitionResponse.From)
        .ToList();
}

public sealed record UpdateModuleDefinitionCommand(
    string Code, string Label, string Description, decimal MonthlyPrice, string Currency, bool IsAvailable)
    : IRequest<ModuleResult>;

internal sealed class UpdateModuleDefinitionHandler(AdminDbContext db, ILogger<UpdateModuleDefinitionHandler> logger)
    : IRequestHandler<UpdateModuleDefinitionCommand, ModuleResult>
{
    public async Task<ModuleResult> Handle(UpdateModuleDefinitionCommand cmd, CancellationToken ct)
    {
        if (Invalid(cmd) is { } problem) return ModuleResult.Fail(ModuleErrors.InvalidDefinition, problem);

        var definition = await db.ModuleDefinitions.FirstOrDefaultAsync(m => m.Code == cmd.Code, ct);
        if (definition is null) return ModuleResult.Fail(ModuleErrors.ModuleUnknown);

        var previous = (definition.MonthlyPrice, definition.Currency, definition.IsAvailable);
        definition.Update(cmd.Label, cmd.Description, cmd.MonthlyPrice, cmd.Currency, cmd.IsAvailable,
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Module {Module}: price {OldPrice} {OldCurrency} -> {NewPrice} {NewCurrency}, available {OldAvailable} -> {NewAvailable}",
            cmd.Code, previous.MonthlyPrice, previous.Currency, definition.MonthlyPrice, definition.Currency,
            previous.IsAvailable, definition.IsAvailable);
        return ModuleResult.Ok();
    }

    private static string? Invalid(UpdateModuleDefinitionCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Label) || cmd.Label.Trim().Length > ModuleDefinition.LabelMaxLength)
            return $"Label is required and at most {ModuleDefinition.LabelMaxLength} characters.";
        if ((cmd.Description?.Trim().Length ?? 0) > ModuleDefinition.DescriptionMaxLength)
            return $"Description is at most {ModuleDefinition.DescriptionMaxLength} characters.";
        if (cmd.MonthlyPrice < 0 || decimal.Round(cmd.MonthlyPrice, 2) != cmd.MonthlyPrice)
            return "MonthlyPrice must be zero or positive, with at most two decimals.";
        var currency = cmd.Currency?.Trim() ?? string.Empty;
        if (currency.Length != ModuleDefinition.CurrencyLength || !currency.All(char.IsAsciiLetter))
            return "Currency must be a three-letter ISO 4217 code, e.g. XOF.";
        return null;
    }
}
