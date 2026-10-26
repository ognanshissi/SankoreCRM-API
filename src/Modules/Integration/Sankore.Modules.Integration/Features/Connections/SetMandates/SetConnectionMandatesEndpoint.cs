namespace Sankore.Modules.Integration.Features.Connections.SetMandates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The insurer's mandates to the distributor (insurance lot 2): collecting premiums in its name,
/// issuing cover notes. Without the first, a premium cannot be debited or recorded by the agent
/// for this insurer — the insurer collects it and the contract waits « en attente de paiement ».
/// </summary>
internal static class SetConnectionMandatesEndpoint
{
    internal static IEndpointRouteBuilder MapSetConnectionMandates(this IEndpointRouteBuilder app)
    {
        app.MapPut("{connectionId:guid}/mandates", Handle)
            .WithName("SetIntegrationConnectionMandates")
            .WithSummary("Record the insurer's collection and cover-note mandates")
            .WithDescription(
                "Insurance connections only. Each mandate granted carries the reference of the "
                + "document that grants it (400 INTEGRATION_MANDATE_REFERENCE_REQUIRED otherwise). "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPut("{connectionId:guid}/commission-netting", SetNetting)
            .WithName("SetIntegrationConnectionCommissionNetting")
            .WithSummary("Record the insurer's agreement that commissions are deducted from remitted premiums")
            .WithDescription(
                "Without it the full premiums are remitted and the commission paid on the insurer's "
                + "statement. The agreement carries its reference. Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> SetNetting(
        Guid connectionId, SetCommissionNettingRequest body, IntegrationDbContext db, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        var connection = await db.Connections.AsTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
            return Results.NotFound(new { error = IntegrationErrors.ConnectionNotFound });

        var set = connection.SetCommissionNetting(body.Agreed, body.Reference, user.Id, clock);
        if (set.IsFailure)
            return Results.BadRequest(new { error = set.Error });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        SetConnectionMandatesRequest body,
        IntegrationDbContext db,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken ct)
    {
        var connection = await db.Connections.AsTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);

        if (connection is null)
            return Results.NotFound(new { error = IntegrationErrors.ConnectionNotFound });

        var set = connection.SetMandates(
            body.HasCollectionMandate, body.CollectionMandateReference,
            body.HasCoverNoteMandate, body.CoverNoteMandateReference,
            user.Id, clock);

        if (set.IsFailure)
            return Results.BadRequest(new { error = set.Error });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public sealed record SetConnectionMandatesRequest(
    bool HasCollectionMandate,
    string? CollectionMandateReference,
    bool HasCoverNoteMandate,
    string? CoverNoteMandateReference);

public sealed record SetCommissionNettingRequest(bool Agreed, string? Reference);
