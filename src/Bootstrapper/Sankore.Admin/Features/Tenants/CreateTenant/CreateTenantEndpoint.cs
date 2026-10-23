using MediatR;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Admin.Features.Tenants.CreateTenant;

public static class CreateTenantEndpoint
{
    /// <param name="ActiveModules">Optional <see cref="TenantModules"/> codes switched on at creation.</param>
    /// <param name="Profile">
    /// Optional creation profile (<see cref="TenantProfiles"/>), the usual way to choose the modules.
    /// Sending it together with <paramref name="ActiveModules"/> is refused: two answers to one question.
    /// </param>
    public record Request(
        string Name,
        string RootUserEmail,
        string Fqdn,
        DateTimeOffset? TrialExpiresAt,
        IReadOnlyList<string>? ActiveModules = null,
        string? Profile = null);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/", async (Request req, HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                if (req.ActiveModules?.FirstOrDefault(m => !TenantModules.IsKnown(m)) is { } unknown)
                    return Results.BadRequest(new { error = "MODULE_UNKNOWN", module = unknown });

                var (modules, profileError) = TenantProfiles.ResolveModules(req.Profile, req.ActiveModules);
                if (profileError is not null)
                    return Results.BadRequest(new { error = profileError, profile = req.Profile });

                try
                {
                    var created = await mediator.Send(
                        new CreateTenantCommand(req.Name, req.RootUserEmail, req.Fqdn, req.TrialExpiresAt,
                            modules, AdminClaims.AdminId(http.User)), ct);
                    return Results.Created($"/api/v1/tenants/{created.Id}", new { id = created.Id });
                }
                catch (InvalidOperationException)
                {
                    // The handler's duplicate-FQDN refusal: a conflict the operator can fix, not a 500.
                    return Results.Conflict(new { error = "TENANT_FQDN_TAKEN", fqdn = req.Fqdn });
                }
            })
            .WithName("CreateTenant")
            .WithSummary("Create a new tenant")
            .WithDescription("Profile (optional: ImfOrBank, CaisseWithInsurance, InsuranceDistributor, CrmOnly — "
                + "GET /api/v1/tenant-profiles) or ActiveModules (codes: BankingMicrofinance, Insurance) choose "
                + "the paid modules switched on at creation; not both. Neither: the socle alone. "
                + "409 TENANT_FQDN_TAKEN when the address belongs to another tenant.")
            .Produces<object>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();
    }
}
