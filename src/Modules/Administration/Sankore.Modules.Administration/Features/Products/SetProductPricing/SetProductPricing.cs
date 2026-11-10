namespace Sankore.Modules.Administration.Features.Products.SetProductPricing;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Price, unit and VAT category of a Standard product (quote lines, lot 1, 2026-11-10, product owner):
/// what a quote line will propose by default. A full replacement — the three go together on the
/// product screen's « Prix et TVA » card.
/// </summary>
internal sealed record SetProductPricingCommand(
    Guid ProductId,
    decimal? UnitPrice,
    string? PriceUnit,
    ProductVatCategory VatCategory
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductSpeciality";
    public string? ResourceId => ProductId.ToString();
}

internal sealed class SetProductPricingValidator : AbstractValidator<SetProductPricingCommand>
{
    public SetProductPricingValidator()
    {
        RuleFor(c => c.ProductId).NotEmpty();
        RuleFor(c => c.UnitPrice).GreaterThanOrEqualTo(0).When(c => c.UnitPrice.HasValue)
            .WithMessage("Le prix unitaire ne peut pas être négatif.");
        RuleFor(c => c.PriceUnit).MaximumLength(ProductSpeciality.MaxPriceUnitLength)
            .WithMessage($"L’unité tient en {ProductSpeciality.MaxPriceUnitLength} caractères au plus.");
        RuleFor(c => c.VatCategory).IsInEnum();
    }
}

internal sealed class SetProductPricingHandler(
    AdministrationDbContext db,
    ProductCatalogCache catalogCache)
    : IRequestHandler<SetProductPricingCommand, Result>
{
    public async Task<Result> Handle(SetProductPricingCommand cmd, CancellationToken ct)
    {
        var product = await db.ProductSpecialities.AsTracking()
            .FirstOrDefaultAsync(p => p.Id == cmd.ProductId, ct);
        if (product is null) return Result.Fail("PRODUCT_NOT_FOUND");

        var set = product.SetPricing(cmd.UnitPrice, cmd.PriceUnit, cmd.VatCategory);
        if (set.IsFailure) return set;

        await db.SaveChangesAsync(ct);

        // After SaveChanges, like every product write (see SetKycRemotePolicyHandler).
        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);
        return Result.Ok();
    }
}

/// <param name="UnitPrice">Excluding VAT, in the institution's currency; null clears it.</param>
/// <param name="PriceUnit">« pièce », « carton », « heure »… ; null or blank clears it.</param>
/// <param name="VatCategory">Standard (default), Reduced or Exempt.</param>
public sealed record SetProductPricingRequest(
    decimal? UnitPrice = null,
    string? PriceUnit = null,
    ProductVatCategory VatCategory = ProductVatCategory.Standard);

internal static class SetProductPricingEndpoint
{
    public static IEndpointRouteBuilder MapSetProductPricing(this IEndpointRouteBuilder app)
    {
        app.MapPut("{productId:guid}/pricing", Handle)
            .WithName("SetProductPricing")
            .WithSummary("Set the unit price, unit and VAT category of a Standard product")
            .WithDescription(
                "Standard products only (PRODUCT_PRICING_STANDARD_ONLY otherwise): a credit, savings or "
                + "insurance line has no unit price. The price is excluding VAT, in the institution's "
                + "currency; the rates themselves are the institution's (company-info/pricing). "
                + "Requires permission: product:update.")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Accepts<SetProductPricingRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();
        return app;
    }

    private static async Task<IResult> Handle(
        Guid productId, SetProductPricingRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new SetProductPricingCommand(productId, req.UnitPrice, req.PriceUnit, req.VatCategory), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "PRODUCT_NOT_FOUND" => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error }),
            };
    }
}
