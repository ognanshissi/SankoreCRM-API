namespace Sankore.Modules.Leads.Features.Import;

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

public static class GetImportStatusEndpoint
{
    public static IEndpointRouteBuilder MapGetImportStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("import/{id:guid}", Handle)
            .WithName("GetImportStatus")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Produces<LeadImportStatusResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        LeadsDbContext db,
        CancellationToken ct)
    {
        var job = await db.LeadImportJobs
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return Results.NotFound();

        List<ImportRowFailure>? failures = null;
        if (job.FailureDetailsJson is not null)
        {
            failures = JsonSerializer.Deserialize<List<ImportRowFailure>>(
                job.FailureDetailsJson);
        }

        return Results.Ok(new LeadImportStatusResponse(
            ImportJobId: job.Id,
            Status: job.Status,
            OriginalFileName: job.OriginalFileName,
            TotalRows: job.TotalRows,
            Succeeded: job.Succeeded,
            Skipped: job.Skipped,
            Failed: job.Failed,
            CreatedAt: job.CreatedAt,
            CompletedAt: job.CompletedAt,
            ErrorMessage: job.ErrorMessage,
            Failures: failures));
    }
}
