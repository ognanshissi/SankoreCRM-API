namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

public static class SdkEndpoints
{
    /// <summary>
    /// CORS policy of the two SERVE routes, and the only one in the platform that allows any
    /// origin.
    ///
    /// <para>
    /// The embed snippet emits <c>integrity</c> + <c>crossorigin="anonymous"</c> (see
    /// <c>SnippetBuilder</c>), which makes the browser fetch the script as a CORS request and
    /// refuse it unless the response carries <c>Access-Control-Allow-Origin</c>. The host's
    /// DEFAULT policy is an allow-list of the institution's own front-ends, and a customer's
    /// site is never in it — so under the default policy the snippet could not load on a single
    /// third-party page, which is the only place it is ever pasted. The symptom is the browser
    /// blaming CORS for a file that is served perfectly: <c>blocked by CORS policy: No
    /// 'Access-Control-Allow-Origin' header</c>.
    /// </para>
    ///
    /// <para>
    /// A wildcard is safe HERE and nowhere else in this API: the response is a public static
    /// script, identical for every tenant, carries no credentials (the policy must NOT call
    /// <c>AllowCredentials</c> — a wildcard is incompatible with it, and the browser would reject
    /// the response at runtime) and reveals nothing a <c>curl</c> does not. The ingest endpoint
    /// that the script then POSTs to keeps its per-source origin check in
    /// <c>WebIngestCorsEndpoint</c>; that is where an allow-list belongs, because that one writes.
    /// </para>
    /// </summary>
    public const string PublicCorsPolicy = "sankore-sdk-public";

    /// <summary>
    /// Registers <see cref="PublicCorsPolicy"/>. Declared by the module that owns the routes
    /// rather than by the host's <c>CorsSetup</c>: the two must not drift, and a named policy a
    /// route requires but nobody registered throws on the first request.
    /// <see cref="Microsoft.Extensions.DependencyInjection.CorsServiceCollectionExtensions.AddCors(IServiceCollection, System.Action{Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions})"/>
    /// is additive, so this adds the named policy without touching the host's default one.
    /// </summary>
    public static IServiceCollection AddSdkServeCors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCors(options => options.AddPolicy(PublicCorsPolicy, policy =>
            policy.AllowAnyOrigin()
                  .WithMethods("GET", "HEAD")
                  .AllowAnyHeader()));

        return services;
    }

    /// <summary>
    /// Admin endpoints under api/v1/leads/sdk (authenticated).
    /// </summary>
    internal static IEndpointRouteBuilder MapSdkAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sdk/versions").WithTags("SDK");

        group.MapGet("", ListVersions)
            .WithName("ListSdkVersions")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .WithOpenApi();

        group.MapPost("", PublishVersion)
            .WithName("PublishSdkVersion")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<PublishSdkVersionResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery()
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// Public endpoints for serving SDK files (no auth).
    /// </summary>
    public static IEndpointRouteBuilder MapSdkServeEndpoints(this IEndpointRouteBuilder app)
    {
        // Exact version: /sdk/{version}/forms.min.js → immutable cache
        app.MapGet("/sdk/{version}/forms.min.js", ServeExactVersion)
            .AllowAnonymous()
            .RequireCors(PublicCorsPolicy)
            .WithName("ServeSdkExact")
            .WithTags("SDK")
            .ExcludeFromDescription();

        // Major alias: /sdk/v{major}/forms.min.js → short cache, resolves to current
        // La source lisible, pour qu'un intégrateur puisse lire ce qu'il charge et déboguer.
        // Jamais destinée à être épinglée : seul forms.min.js porte l'empreinte SRI enregistrée.
        app.MapGet("/sdk/{version}/forms.js", ServeReadableSource)
            .AllowAnonymous()
            .WithName("ServeSdkSource")
            .WithTags("SDK")
            .ExcludeFromDescription();

        app.MapGet("/sdk/v{major:int}/forms.min.js", ServeMajorAlias)
            .AllowAnonymous()
            .RequireCors(PublicCorsPolicy)
            .WithName("ServeSdkMajor")
            .WithTags("SDK")
            .ExcludeFromDescription();

        return app;
    }

    // ── Handlers ─────────────────────────────────────────────────────────

    private static async Task<IResult> ListVersions(
        LeadsDbContext db, CancellationToken ct)
    {
        var versions = await db.SdkVersions
            .IgnoreQueryFilters()
            .OrderByDescending(v => v.PublishedAt)
            .Select(v => new SdkVersionDto(v.Id, v.Version, v.Major, v.SriHash, v.IsCurrent, v.PublishedAt))
            .ToListAsync(ct);

        return Results.Ok(versions);
    }

    private static async Task<IResult> PublishVersion(
        HttpRequest request, ISender sender, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var version = form["version"].ToString();
        var majorStr = form["major"].ToString();
        var file = form.Files.GetFile("file");

        if (string.IsNullOrEmpty(version) || !int.TryParse(majorStr, out var major) || file is null)
            return Results.Problem(detail: "version, major (int), and file are required.", statusCode: 422);

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var result = await sender.Send(
            new PublishSdkVersionCommand(version, major, ms.ToArray()), ct);

        return result.IsSuccess
            ? Results.Created($"/sdk/{version}/forms.min.js", result.Value)
            : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ServeExactVersion(
        string version, HttpContext httpContext, ISdkFileStore fileStore, CancellationToken ct)
    {
        var content = await fileStore.ReadAsync(version, "forms.min.js", ct);
        if (content is null) return Results.NotFound();

        // Immutable — exact version never changes
        httpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(content, "text/javascript");
    }

    private static async Task<IResult> ServeReadableSource(
        string version, HttpContext httpContext, ISdkFileStore fileStore, CancellationToken ct)
    {
        var content = await fileStore.ReadAsync(version, "forms.js", ct);
        if (content is null) return Results.NotFound();

        httpContext.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(content, "text/javascript");
    }

    private static async Task<IResult> ServeMajorAlias(
        int major, HttpContext httpContext, LeadsDbContext db, ISdkFileStore fileStore, CancellationToken ct)
    {
        var current = await db.SdkVersions
            .IgnoreQueryFilters()
            .Where(v => v.Major == major && v.IsCurrent)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(ct);

        if (current is null) return Results.NotFound();

        var content = await fileStore.ReadAsync(current, "forms.min.js", ct);
        if (content is null) return Results.NotFound();

        // Short cache — alias may point to a new version after publish
        httpContext.Response.Headers.CacheControl = "public, max-age=300";
        return Results.File(content, "text/javascript");
    }
}

public sealed record SdkVersionDto(
    Guid Id, string Version, int Major, string SriHash, bool IsCurrent, DateTimeOffset PublishedAt);
