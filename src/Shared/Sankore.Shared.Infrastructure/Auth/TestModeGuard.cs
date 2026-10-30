namespace Sankore.Shared.Infrastructure.Auth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The team is changed in production only (sandbox lot 2, product owner): users, roles, agencies,
/// territories, the institution's identity and its modules are SHARED by both modes, so a change
/// made from « Mode test » would land in production while the screen says « test ». Every
/// non-reading call of a guarded group is refused under a test-mode token.
/// </summary>
public static class TestModeGuard
{
    /// <summary>Relayed to the front as the problem's <c>detail</c>: never rename it.</summary>
    public const string TeamReadOnly = "TEST_MODE_TEAM_READ_ONLY";

    /// <param name="exempt">A path of the group the caller may still change in test mode — their own profile.</param>
    public static TBuilder LiveModeWritesOnly<TBuilder>(this TBuilder builder, Func<HttpContext, bool>? exempt = null)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var reading = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

            if (!reading && IsTestMode(http) && exempt?.Invoke(http) != true)
            {
                return Results.Problem(
                    title: "En mode test, l’équipe et l’institution se modifient en production.",
                    detail: TeamReadOnly,
                    statusCode: StatusCodes.Status409Conflict);
            }

            return await next(context);
        });

    public static bool IsTestMode(HttpContext http) => http.User.HasClaim(c => c.Type == TestModeClaims.TeamTenantId);
}
