namespace Sankore.Modules.Administration.Features.ImportUsers;

using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ImportUsersEndpoints
{
    public static IEndpointRouteBuilder MapImportUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("users/import").WithTags("User Import");

        // POST /users/import/file (CSV or Excel upload)
        group.MapPost("file", ImportFromFile)
            .WithName("ImportUsersFromFile")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        // POST /users/import/google-sheet
        group.MapPost("google-sheet", ImportFromGoogleSheet)
            .WithName("ImportUsersFromGoogleSheet")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader();

        // POST /users/import/google-contacts
        group.MapPost("google-contacts", ImportFromGoogleContacts)
            .WithName("ImportUsersFromGoogleContacts")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader();

        // GET /users/import/{id}/status
        group.MapGet("{id:guid}/status", GetImportStatus)
            .WithName("GetUserImportStatus")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<UserImportStatusDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> ImportFromFile(
        IFormFile file,
        AdministrationDbContext db,
        IUserImportFileStore fileStore,
        ICurrentUser currentUser,
        ITenantContext tenant,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.Problem("File is empty.", statusCode: 400);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx"))
            return Results.Problem("Only .csv and .xlsx files are supported.", statusCode: 400);

        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        var job = UserImportJob.Create(
            tenant.CurrentTenantId, currentUser.Id,
            UserImportSourceType.File, fileRef, clock, file.FileName);

        db.UserImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessUserImportJob>(
            j => j.ExecuteAsync(job.Id, tenant.CurrentTenantId, currentUser.Id));

        return Results.Accepted(
            $"users/import/{job.Id}/status",
            new ImportJobCreatedResult(job.Id));
    }

    private static async Task<IResult> ImportFromGoogleSheet(
        GoogleSheetImportRequest req,
        AdministrationDbContext db,
        ICurrentUser currentUser,
        ITenantContext tenant,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        CancellationToken ct)
    {
        var job = UserImportJob.Create(
            tenant.CurrentTenantId, currentUser.Id,
            UserImportSourceType.GoogleSheet, req.SpreadsheetUrl, clock);

        db.UserImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessUserImportJob>(
            j => j.ExecuteAsync(job.Id, tenant.CurrentTenantId, currentUser.Id));

        return Results.Accepted(
            $"users/import/{job.Id}/status",
            new ImportJobCreatedResult(job.Id));
    }

    private static async Task<IResult> ImportFromGoogleContacts(
        AdministrationDbContext db,
        ICurrentUser currentUser,
        ITenantContext tenant,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        CancellationToken ct)
    {
        var job = UserImportJob.Create(
            tenant.CurrentTenantId, currentUser.Id,
            UserImportSourceType.GoogleContacts, "google-contacts", clock);

        db.UserImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        hangfire.Enqueue<ProcessUserImportJob>(
            j => j.ExecuteAsync(job.Id, tenant.CurrentTenantId, currentUser.Id));

        return Results.Accepted(
            $"users/import/{job.Id}/status",
            new ImportJobCreatedResult(job.Id));
    }

    private static async Task<IResult> GetImportStatus(
        Guid id, AdministrationDbContext db, CancellationToken ct)
    {
        var job = await db.UserImportJobs
            .Where(j => j.Id == id)
            .Select(j => new UserImportStatusDto(
                j.Id, j.SourceType, j.Status,
                j.TotalRows, j.Succeeded, j.Skipped, j.Failed,
                j.ErrorMessage, j.CreatedAt, j.CompletedAt))
            .FirstOrDefaultAsync(ct);

        return job is null ? Results.NotFound() : Results.Ok(job);
    }
}

public sealed record ImportJobCreatedResult(Guid ImportJobId);

public sealed record GoogleSheetImportRequest(string SpreadsheetUrl);

public sealed record UserImportStatusDto(
    Guid Id,
    UserImportSourceType SourceType,
    UserImportStatus Status,
    int TotalRows,
    int Succeeded,
    int Skipped,
    int Failed,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
