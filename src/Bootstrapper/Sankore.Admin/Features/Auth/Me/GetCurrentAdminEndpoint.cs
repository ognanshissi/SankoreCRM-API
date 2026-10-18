using System.Security.Claims;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Auth.Me;

public static class GetCurrentAdminEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/me", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                if (AdminClaims.AdminId(user) is not { } adminId)
                    return Results.Unauthorized();

                // A disabled account keeps a valid access token until it expires; answering 401
                // here is what makes the front-end drop the session on its next navigation.
                var admin = await sender.Send(new GetCurrentAdminQuery(adminId), ct);
                return admin is null ? Results.Unauthorized() : Results.Ok(admin);
            })
            .WithName("GetCurrentAdmin")
            .WithSummary("The signed-in platform administrator")
            .Produces<CurrentAdminResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization()
            .WithOpenApi();
    }
}

public sealed record CurrentAdminResponse(Guid Id, string Email, string FullName, DateTimeOffset? LastLoginAt);

public sealed record GetCurrentAdminQuery(Guid AdminId) : IRequest<CurrentAdminResponse?>;

internal sealed class GetCurrentAdminHandler(AdminDbContext db) : IRequestHandler<GetCurrentAdminQuery, CurrentAdminResponse?>
{
    public Task<CurrentAdminResponse?> Handle(GetCurrentAdminQuery request, CancellationToken ct) =>
        db.PlatformAdmins.AsNoTracking()
            .Where(a => a.Id == request.AdminId && a.IsActive)
            .Select(a => new CurrentAdminResponse(a.Id, a.Email, a.FullName, a.LastLoginAt))
            .FirstOrDefaultAsync(ct);
}
