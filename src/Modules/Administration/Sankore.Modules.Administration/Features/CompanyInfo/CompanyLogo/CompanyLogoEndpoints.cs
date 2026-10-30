using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.CompanyLogo;

/// <summary>
/// Dépôt, retrait et diffusion du logo de l'institution.
///
/// <para>
/// Le dépôt réécrit <c>company_info.logo_url</c> vers la route publique de diffusion, en URL
/// ABSOLUE : cette valeur est lue telle quelle par le front, par la page de connexion (contexte du
/// tenant) et par les modèles d'e-mail, qui n'ont pas d'origine à laquelle rattacher un chemin
/// relatif. Le paramètre <c>v</c> change à chaque dépôt, ce qui laisse la réponse en cache longue
/// durée sans jamais servir l'ancien logo.
/// </para>
/// </summary>
internal static class CompanyLogoEndpoints
{
    private const string PublicPathTemplate = "/api/v1/public/branding/{0}/logo";

    /// <summary>Sous <c>company-info</c>, authentifié.</summary>
    public static IEndpointRouteBuilder MapCompanyLogo(this IEndpointRouteBuilder app)
    {
        app.MapPut("logo", UploadAsync)
            .WithName("UploadCompanyLogo")
            .WithSummary("Remplace le logo de l'institution (PNG, JPG ou WebP, 1 Mo maximum).")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CompanyLogoResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .DisableAntiforgery();

        app.MapDelete("logo", DeleteAsync)
            .WithName("DeleteCompanyLogo")
            .WithSummary("Retire le logo déposé.")
            .RequireAuthorization(Permissions.CanUpdateCompanyInfo.Code)
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    /// <summary>
    /// Sous <c>api/v1/public</c>, que <c>TenantResolutionMiddleware</c> exempte : un e-mail ouvert
    /// dans une messagerie n'envoie ni jeton ni en-tête de tenant. Le tenant vient donc du chemin,
    /// et la lecture ignore le filtre de requête en le reposant explicitement.
    /// </summary>
    public static IEndpointRouteBuilder MapCompanyLogoPublic(this IEndpointRouteBuilder app)
    {
        app.MapGet("branding/{tenantId:guid}/logo", ServeAsync)
            .WithName("GetPublicCompanyLogo")
            .WithSummary("Le logo d'une institution, pour l'affichage (application, page de connexion, e-mails).")
            .WithTags("Company Info")
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file, HttpRequest request, AdministrationDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        // Refusé avant lecture : inutile de charger un fichier de 50 Mo pour dire qu'il en fallait 1.
        if (file.Length > Domain.CompanyLogo.MaxSizeBytes)
            return Results.Problem("Le logo ne peut pas dépasser 1 Mo.", statusCode: StatusCodes.Status400BadRequest);

        var info = await db.CompanyInfos.AsTracking().FirstOrDefaultAsync(ct);
        if (info is null)
            return Results.NotFound();

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var logo = await db.CompanyLogos.AsTracking().FirstOrDefaultAsync(ct);
        try
        {
            if (logo is null)
            {
                logo = Domain.CompanyLogo.Create(tenant.TeamTenantId, buffer.ToArray());
                db.CompanyLogos.Add(logo);
            }
            else
            {
                logo.Replace(buffer.ToArray());
            }
        }
        catch (DomainException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var url = $"{ExternalOrigin(request)}{string.Format(PublicPathTemplate, tenant.TeamTenantId)}"
                  + $"?v={logo.UpdatedAt.ToUnixTimeMilliseconds()}";
        info.SetLogoUrl(url);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new CompanyLogoResponse(url));
    }

    private static async Task<IResult> DeleteAsync(AdministrationDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        var logo = await db.CompanyLogos.AsTracking().FirstOrDefaultAsync(ct);
        if (logo is not null)
            db.CompanyLogos.Remove(logo);

        // Seulement si l'URL pointe encore vers le logo déposé : une adresse externe saisie depuis
        // ne doit pas disparaître parce qu'on retire un fichier qui ne servait plus.
        var info = await db.CompanyInfos.AsTracking().FirstOrDefaultAsync(ct);
        if (info is not null && info.LogoUrl.Contains(string.Format(PublicPathTemplate, tenant.TeamTenantId), StringComparison.OrdinalIgnoreCase))
            info.SetLogoUrl(string.Empty);

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ServeAsync(Guid tenantId, HttpResponse response, AdministrationDbContext db, CancellationToken ct)
    {
        var logo = await db.CompanyLogos
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => new { l.Content, l.ContentType, l.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        if (logo is null)
            return Results.NotFound();

        // Une semaine : l'URL change à chaque dépôt (`v`), donc une réponse en cache n'est jamais
        // l'ancien logo. `nosniff` parce que le type servi est celui lu dans les octets, et qu'un
        // navigateur ne doit pas en deviner un autre.
        response.Headers.CacheControl = "public, max-age=604800";
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(logo.Content, logo.ContentType, lastModified: logo.UpdatedAt);
    }

    /// <summary>
    /// L'origine telle que le client la voit, derrière le proxy qui termine le TLS — la même
    /// reconstruction que les rappels SMS (<c>SmsWebhooksEndpoints.ExternalUrl</c>). Sans elle, le
    /// logo enregistré pointerait vers <c>http://sankore-api:8080</c>, injoignable hors du réseau.
    /// </summary>
    private static string ExternalOrigin(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } forwarded
            ? forwarded.Split(',')[0].Trim()
            : request.Scheme;
        var host = request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } forwardedHost
            ? forwardedHost.Split(',')[0].Trim()
            : request.Host.Value;
        return $"{scheme}://{host}{request.PathBase}";
    }
}

public sealed record CompanyLogoResponse(string LogoUrl);
