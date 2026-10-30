using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.TestTwin;

/// <summary>
/// Sandbox lot 2: the test twin of a live tenant, created the first time somebody switches to
/// « Mode test » in it. Called by Sankore.Api (API key), never by the platform screens — the
/// tenant's own administrators decide when they need a sandbox. Idempotent: the existing twin is
/// answered, and a second concurrent creation loses on the unique FQDN and reads the winner.
/// </summary>
public static class EnsureTestTwinEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{id:guid}/test-twin", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                var twin = await sender.Send(new EnsureTestTwinCommand(id), ct);
                return twin is null ? Results.NotFound() : Results.Ok(new EnsureTestTwinResponse(twin.Value));
            })
            .WithName("EnsureTestTwin")
            .WithSummary("Create, or answer, the test twin of a live tenant")
            .Produces<EnsureTestTwinResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();
    }
}

public sealed record EnsureTestTwinResponse(Guid Id);

/// <returns>The twin's id; null when the tenant is unknown or is itself a twin.</returns>
public sealed record EnsureTestTwinCommand(Guid TenantId) : IRequest<Guid?>;

internal sealed class EnsureTestTwinHandler(AdminDbContext db, ILogger<EnsureTestTwinHandler> logger)
    : IRequestHandler<EnsureTestTwinCommand, Guid?>
{
    public async Task<Guid?> Handle(EnsureTestTwinCommand request, CancellationToken ct)
    {
        var parent = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId, ct);
        if (parent is null || parent.ParentTenantId is not null) return null;

        var existing = await ExistingAsync(parent.Id, ct);
        if (existing is not null) return existing;

        var twin = Tenant.CreateTestTwin(parent);
        db.Tenants.Add(twin);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first switches at once: the other one created it.
            db.ChangeTracker.Clear();
            return await ExistingAsync(parent.Id, ct);
        }

        logger.LogWarning("Test twin {TwinId} created for tenant {TenantId} ({TenantName})", twin.Id, parent.Id, parent.Name);
        return twin.Id;
    }

    private Task<Guid?> ExistingAsync(Guid parentId, CancellationToken ct)
        => db.Tenants.Where(t => t.ParentTenantId == parentId).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
}
