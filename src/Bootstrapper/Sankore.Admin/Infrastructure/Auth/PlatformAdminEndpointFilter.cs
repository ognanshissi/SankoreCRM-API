namespace Sankore.Admin.Infrastructure.Auth;

/// <summary>
/// Restricts a route to a platform administrator's token.
///
/// <para>
/// The host-wide gate in <c>Program.cs</c> lets through EITHER credential — the service key
/// Sankore.Api holds, or an operator's JWT. That is right for the routes Sankore.Api calls on a
/// tenant's behalf, and wrong for a platform decision: with the key alone, Sankore.Api could
/// authorise its own tenant's request for a paid module, price a module, or switch one on. Those
/// routes carry this filter, and the key is no longer enough for them.
/// </para>
/// </summary>
public static class PlatformAdminEndpointFilter
{
    public const string Error = "PLATFORM_ADMIN_REQUIRED";

    public static TBuilder RequirePlatformAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(Gate);

    /// <summary>The filter itself, separate so a test can drive it without a host.</summary>
    internal static async ValueTask<object?> Gate(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        AdminClaims.IsPlatformAdmin(ctx.HttpContext.User)
            ? await next(ctx)
            : Results.Json(new { error = Error }, statusCode: StatusCodes.Status403Forbidden);
}
