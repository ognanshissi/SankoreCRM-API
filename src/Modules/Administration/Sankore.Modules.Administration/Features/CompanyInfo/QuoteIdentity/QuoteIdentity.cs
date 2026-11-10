namespace Sankore.Modules.Administration.Features.CompanyInfo.QuoteIdentity;

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

// Quote lines, lot 2 (2026-11-10): what a quote prints about the institution, and how its quotes
// are numbered, dated and paid. Two cards, two routes, the « company-info » group's rules
// (read: company-info:read; write: company-info:update, refused in « Mode test »).

/// <summary>The legal identity printed on quotes. Every field is optional.</summary>
public sealed record LegalIdentityDto(
    string? LegalName, string? LegalForm, string? HeadOfficeAddress, string? Phone, string? Email,
    string? Website, string? Rccm, string? TaxpayerNumber, string? TaxRegime, string? QuoteFooter);

/// <param name="NextNumber">What the next quote of this year will be numbered, without taking it.</param>
public sealed record QuoteSettingsDto(string Prefix, int ValidityDays, string? PaymentTerms, string NextNumber);

internal sealed record GetLegalIdentityQuery(Guid TeamTenantId) : IRequest<LegalIdentityDto>;

internal sealed class GetLegalIdentityHandler(AdministrationDbContext db) : IRequestHandler<GetLegalIdentityQuery, LegalIdentityDto>
{
    public async Task<LegalIdentityDto> Handle(GetLegalIdentityQuery q, CancellationToken ct)
    {
        var c = await db.CompanyInfos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == q.TeamTenantId, ct);
        return c is null
            ? new LegalIdentityDto(null, null, null, null, null, null, null, null, null, null)
            : new LegalIdentityDto(c.LegalName, c.LegalForm, c.HeadOfficeAddress, c.LegalPhone, c.LegalEmail,
                c.Website, c.Rccm, c.TaxpayerNumber, c.TaxRegime, c.QuoteFooter);
    }
}

internal sealed record GetQuoteSettingsQuery(Guid TeamTenantId, Guid DataTenantId, int Year) : IRequest<QuoteSettingsDto>;

internal sealed class GetQuoteSettingsHandler(AdministrationDbContext db) : IRequestHandler<GetQuoteSettingsQuery, QuoteSettingsDto>
{
    public async Task<QuoteSettingsDto> Handle(GetQuoteSettingsQuery q, CancellationToken ct)
    {
        var c = await db.CompanyInfos.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == q.TeamTenantId)
            .Select(x => new { x.QuotePrefix, x.QuoteValidityDays, x.QuotePaymentTerms })
            .FirstOrDefaultAsync(ct);
        var prefix = c?.QuotePrefix ?? CompanyInfoEntity.DefaultQuotePrefix;
        var next = await QuoteIssuerReader.PeekAsync(db, q.DataTenantId, prefix, q.Year, ct);
        return new QuoteSettingsDto(prefix, c?.QuoteValidityDays ?? CompanyInfoEntity.DefaultQuoteValidityDays, c?.QuotePaymentTerms, next);
    }
}

internal sealed record SetLegalIdentityCommand(
    string? LegalName, string? LegalForm, string? HeadOfficeAddress, string? Phone, string? Email,
    string? Website, string? Rccm, string? TaxpayerNumber, string? TaxRegime, string? QuoteFooter)
    : IRequest<Result>, ICommand;

internal sealed class SetLegalIdentityValidator : AbstractValidator<SetLegalIdentityCommand>
{
    public SetLegalIdentityValidator()
    {
        RuleFor(c => c.LegalName).MaximumLength(CompanyInfoEntity.MaxLegalNameLength).WithMessage("La raison sociale est trop longue.");
        RuleFor(c => c.LegalForm).MaximumLength(CompanyInfoEntity.MaxLegalFormLength).WithMessage("La forme juridique est trop longue.");
        RuleFor(c => c.HeadOfficeAddress).MaximumLength(CompanyInfoEntity.MaxHeadOfficeAddressLength).WithMessage("L’adresse est trop longue.");
        RuleFor(c => c.Phone).MaximumLength(CompanyInfoEntity.MaxLegalPhoneLength).WithMessage("Le téléphone est trop long.");
        RuleFor(c => c.Email).MaximumLength(CompanyInfoEntity.MaxLegalEmailLength).EmailAddress()
            .When(c => !string.IsNullOrWhiteSpace(c.Email)).WithMessage("Une adresse e-mail valide.");
        RuleFor(c => c.Website).MaximumLength(CompanyInfoEntity.MaxWebsiteLength)
            .Must(BeAWebAddress).When(c => !string.IsNullOrWhiteSpace(c.Website))
            .WithMessage("Une adresse web, par exemple www.exemple.ci.");
        RuleFor(c => c.Rccm).MaximumLength(CompanyInfoEntity.MaxRccmLength).WithMessage("Le RCCM est trop long.");
        RuleFor(c => c.TaxpayerNumber).MaximumLength(CompanyInfoEntity.MaxTaxpayerNumberLength).WithMessage("Le n° de compte contribuable est trop long.");
        RuleFor(c => c.TaxRegime).MaximumLength(CompanyInfoEntity.MaxTaxRegimeLength).WithMessage("Le régime d’imposition est trop long.");
        RuleFor(c => c.QuoteFooter).MaximumLength(CompanyInfoEntity.MaxQuoteFooterLength).WithMessage("Le bas de devis tient en 1 000 caractères.");
    }

    /// <summary>« www.exemple.ci » or « https://exemple.ci »: a host with a dot, http(s) if a scheme is given.</summary>
    internal static bool BeAWebAddress(string? value)
    {
        var v = value!.Trim();
        var withScheme = v.Contains("://", StringComparison.Ordinal) ? v : "https://" + v;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
               && uri.Host.Contains('.') && !v.Contains(' ');
    }
}

internal sealed class SetLegalIdentityHandler(AdministrationDbContext db) : IRequestHandler<SetLegalIdentityCommand, Result>
{
    public async Task<Result> Handle(SetLegalIdentityCommand c, CancellationToken ct)
    {
        var info = await db.CompanyInfos.AsTracking().FirstOrDefaultAsync(ct);
        if (info is null) return Result.Fail("COMPANY_INFO_NOT_FOUND");
        var set = info.SetLegalIdentity(c.LegalName, c.LegalForm, c.HeadOfficeAddress, c.Phone, c.Email,
            c.Website, c.Rccm, c.TaxpayerNumber, c.TaxRegime, c.QuoteFooter);
        if (set.IsFailure) return set;
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record SetQuoteSettingsCommand(string Prefix, int ValidityDays, string? PaymentTerms) : IRequest<Result>, ICommand;

internal sealed class SetQuoteSettingsValidator : AbstractValidator<SetQuoteSettingsCommand>
{
    public SetQuoteSettingsValidator()
    {
        RuleFor(c => c.Prefix).Must(p => CompanyInfoEntity.NormalizeQuotePrefix(p) is not null)
            .WithMessage("De 1 à 10 lettres, chiffres ou tirets, par exemple DEV.");
        RuleFor(c => c.ValidityDays).InclusiveBetween(1, CompanyInfoEntity.MaxQuoteValidityDays)
            .WithMessage("Entre 1 et 365 jours.");
        RuleFor(c => c.PaymentTerms).MaximumLength(CompanyInfoEntity.MaxPaymentTermsLength)
            .WithMessage("Les conditions de paiement tiennent en 500 caractères.");
    }
}

internal sealed class SetQuoteSettingsHandler(AdministrationDbContext db) : IRequestHandler<SetQuoteSettingsCommand, Result>
{
    public async Task<Result> Handle(SetQuoteSettingsCommand c, CancellationToken ct)
    {
        var info = await db.CompanyInfos.AsTracking().FirstOrDefaultAsync(ct);
        if (info is null) return Result.Fail("COMPANY_INFO_NOT_FOUND");
        var set = info.SetQuoteSettings(c.Prefix, c.ValidityDays, c.PaymentTerms);
        if (set.IsFailure) return set;
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed record SetLegalIdentityRequest(
    string? LegalName = null, string? LegalForm = null, string? HeadOfficeAddress = null, string? Phone = null,
    string? Email = null, string? Website = null, string? Rccm = null, string? TaxpayerNumber = null,
    string? TaxRegime = null, string? QuoteFooter = null);

public sealed record SetQuoteSettingsRequest(string Prefix, int ValidityDays, string? PaymentTerms = null);

internal static class QuoteIdentityEndpoints
{
    public static IEndpointRouteBuilder MapQuoteIdentity(this IEndpointRouteBuilder app)
    {
        app.MapGet("legal-identity", async (ISender sender, ITenantContext tenant, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetLegalIdentityQuery(tenant.TeamTenantId), ct)))
            .WithName("GetLegalIdentity")
            .WithSummary("The institution's legal identity, printed on its quotes")
            .RequireAuthorization(Permissions.CanReadCompanyInfo.Code)
            .Produces<LegalIdentityDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        app.MapPut("legal-identity", async (SetLegalIdentityRequest r, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new SetLegalIdentityCommand(r.LegalName, r.LegalForm, r.HeadOfficeAddress,
                    r.Phone, r.Email, r.Website, r.Rccm, r.TaxpayerNumber, r.TaxRegime, r.QuoteFooter), ct)))
            .WithName("SetLegalIdentity")
            .WithSummary("Set the institution's legal identity (raison sociale, siège, RCCM, NCC…)")
            .WithDescription("Replaced whole; every field is optional and a blank one clears it. The code imposes no mandatory mention. Requires permission: company-info:update.")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Accepts<SetLegalIdentityRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("quote-settings", async (ISender sender, ITenantContext tenant, TimeProvider clock, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetQuoteSettingsQuery(tenant.TeamTenantId, tenant.CurrentTenantId, clock.GetUtcNow().Year), ct)))
            .WithName("GetQuoteSettings")
            .WithSummary("Numbering, validity and payment terms of the institution's quotes")
            .WithDescription("NextNumber is what the next quote of this year will take, read without taking it. Requires permission: company-info:read.")
            .RequireAuthorization(Permissions.CanReadCompanyInfo.Code)
            .Produces<QuoteSettingsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        app.MapPut("quote-settings", async (SetQuoteSettingsRequest r, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new SetQuoteSettingsCommand(r.Prefix, r.ValidityDays, r.PaymentTerms), ct)))
            .WithName("SetQuoteSettings")
            .WithSummary("Set the prefix, default validity and default payment terms of quotes")
            .WithDescription("Numbers are DEV-2026-000001: prefix, year, rank in the year, starting again each January. Changing the prefix does not renumber quotes already issued. Requires permission: company-info:update.")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Accepts<SetQuoteSettingsRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
        return app;
    }

    private static IResult ToResult(Result result) => result.IsSuccess
        ? Results.NoContent()
        : result.Error == "COMPANY_INFO_NOT_FOUND"
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
}
