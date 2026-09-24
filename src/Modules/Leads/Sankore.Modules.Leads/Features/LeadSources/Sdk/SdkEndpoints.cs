namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

public static class SdkEndpoints
{
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
            .WithName("ServeSdkExact")
            .WithTags("SDK")
            .ExcludeFromDescription();

        // Major alias: /sdk/v{major}/forms.min.js → short cache, resolves to current
        app.MapGet("/sdk/v{major:int}/forms.min.js", ServeMajorAlias)
            .AllowAnonymous()
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
