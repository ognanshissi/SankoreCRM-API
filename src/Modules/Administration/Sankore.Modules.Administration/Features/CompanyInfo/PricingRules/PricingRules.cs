namespace Sankore.Modules.Administration.Features.CompanyInfo.PricingRules;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using CompanyInfoEntity = Sankore.Modules.Administration.Domain.CompanyInfo;

/// <summary>
/// The institution's VAT rates and discount ceiling (quote lines, lot 1, 2026-11-10, product owner).
/// Read by every business role — an agent composing a quote needs the rates — and set by whoever
/// may change the institution's identity.
/// </summary>
/// <param name="MaxDiscountPercent">Null = no ceiling set: no discount needs an approval.</param>
public sealed record PricingRulesDto(decimal VatStandardRate, decimal VatReducedRate, decimal? MaxDiscountPercent);

internal sealed record GetPricingRulesQuery(Guid TenantId) : IRequest<PricingRulesDto>;

internal sealed class GetPricingRulesHandler(AdministrationDbContext db)
    : IRequestHandler<GetPricingRulesQuery, PricingRulesDto>
{
    public async Task<PricingRulesDto> Handle(GetPricingRulesQuery q, CancellationToken ct) =>
        await db.CompanyInfos.IgnoreQueryFilters()
            .Where(c => c.TenantId == q.TenantId)
            .Select(c => new PricingRulesDto(c.VatStandardRate, c.VatReducedRate, c.MaxDiscountPercent))
            .FirstOrDefaultAsync(ct)
        // An institution whose identity was never saved reads the defaults, as a new row would.
        ?? new PricingRulesDto(CompanyInfoEntity.DefaultVatStandardRate, CompanyInfoEntity.DefaultVatReducedRate, null);
}

internal sealed record SetPricingRulesCommand(decimal VatStandardRate, decimal VatReducedRate, decimal? MaxDiscountPercent)
    : IRequest<Result>, ICommand;

internal sealed class SetPricingRulesValidator : AbstractValidator<SetPricingRulesCommand>
{
    public SetPricingRulesValidator()
    {
        RuleFor(c => c.VatStandardRate).InclusiveBetween(0, 100)
            .WithMessage("Le taux normal est un pourcentage entre 0 et 100.");
        RuleFor(c => c.VatReducedRate).InclusiveBetween(0, 100)
            .WithMessage("Le taux réduit est un pourcentage entre 0 et 100.");
        RuleFor(c => c.VatReducedRate).LessThanOrEqualTo(c => c.VatStandardRate)
            .WithMessage("Le taux réduit ne peut pas dépasser le taux normal.");
        RuleFor(c => c.MaxDiscountPercent!.Value).InclusiveBetween(0, 100)
            .When(c => c.MaxDiscountPercent.HasValue)
            .WithName(nameof(SetPricingRulesCommand.MaxDiscountPercent))
            .WithMessage("Le plafond de remise est un pourcentage entre 0 et 100.");
    }
}

internal sealed class SetPricingRulesHandler(AdministrationDbContext db)
    : IRequestHandler<SetPricingRulesCommand, Result>
{
    public async Task<Result> Handle(SetPricingRulesCommand cmd, CancellationToken ct)
    {
        // Same row as UpdateCompanyInfoHandler: the team's, through the query filter.
        var info = await db.CompanyInfos.AsTracking().FirstOrDefaultAsync(ct);
        if (info is null) return Result.Fail("COMPANY_INFO_NOT_FOUND");

        var set = info.SetPricingRules(cmd.VatStandardRate, cmd.VatReducedRate, cmd.MaxDiscountPercent);
        if (set.IsFailure) return set;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed record SetPricingRulesRequest(decimal VatStandardRate, decimal VatReducedRate, decimal? MaxDiscountPercent = null);

internal static class PricingRulesEndpoints
{
    public static IEndpointRouteBuilder MapPricingRules(this IEndpointRouteBuilder app)
    {
        app.MapGet("pricing", async (ISender sender, ITenantContext tenant, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetPricingRulesQuery(tenant.TeamTenantId), ct)))
            .WithName("GetPricingRules")
            .WithSummary("The institution's VAT rates and discount ceiling")
            .WithDescription("Defaults 18 % and 9 % (Côte d'Ivoire) until the institution sets its own. Requires permission: company-info:read.")
            .RequireAuthorization(Permissions.CanReadCompanyInfo.Code)
            .Produces<PricingRulesDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        app.MapPut("pricing", async (SetPricingRulesRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new SetPricingRulesCommand(req.VatStandardRate, req.VatReducedRate, req.MaxDiscountPercent), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : result.Error == "COMPANY_INFO_NOT_FOUND"
                        ? Results.NotFound(new { error = result.Error })
                        : Results.BadRequest(new { error = result.Error });
            })
            .WithName("SetPricingRules")
            .WithSummary("Set the institution's VAT rates and discount ceiling")
            .WithDescription(
                "Rates between 0 and 100, the reduced one not above the standard one (VAT_RATES_INVALID); "
                + "ceiling between 0 and 100 or null (MAX_DISCOUNT_INVALID). Lines already on a project keep "
                + "the rate of their entry. Requires permission: company-info:update.")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Accepts<SetPricingRulesRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
        return app;
    }
}
