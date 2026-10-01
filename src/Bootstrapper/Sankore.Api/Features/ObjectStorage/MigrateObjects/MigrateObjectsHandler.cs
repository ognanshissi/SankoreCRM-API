namespace Sankore.Api.Features.ObjectStorage.MigrateObjects;

using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Validates the request and hands the copy to Hangfire. It moves no byte itself: a volume holding
/// tens of thousands of documents takes far longer than any request may, and a migration that dies
/// with its HTTP connection is one that gets restarted from zero.
/// </summary>
internal sealed class MigrateObjectsHandler(
    IServiceProvider services,
    IConfiguration config,
    IHostEnvironment environment,
    ICurrentUser currentUser,
    IBackgroundJobClient backgroundJobs,
    ILogger<MigrateObjectsHandler> logger)
    : IRequestHandler<MigrateObjectsCommand, Result<MigrateObjectsResult>>
{
    public Task<Result<MigrateObjectsResult>> Handle(MigrateObjectsCommand cmd, CancellationToken ct)
        => Task.FromResult(Decide(cmd));

    private Result<MigrateObjectsResult> Decide(MigrateObjectsCommand cmd)
    {
        // Platform scope, not tenant scope. The copy walks every tenant's prefix, so it is not an
        // operation a tenant's own administrator may start — and RoleSeeder grants every
        // permission to Administrator, which means the permission on the endpoint cannot be the
        // thing that draws that line. Checked here, where the role is visible.
        // Roles.System.CODE, not .Name: RoleItem is (Code, Name) and Name is the French label
        // ("Compte technique"). A JWT role claim carries the Code, so comparing against Name would
        // refuse every real caller — including the one account allowed through here.
        if (!currentUser.Roles.Contains(Roles.System.Code, StringComparer.Ordinal))
            return Result.Fail<MigrateObjectsResult>(
                "OBJECT_STORAGE_MIGRATION_REQUIRES_SYSTEM_ROLE: moving stored objects affects every "
                + "tenant of this installation and is reserved to the platform's System account.");

        var concern = ObjectStorageConcerns.Find(cmd.Concern);
        if (concern is null)
            return Result.Fail<MigrateObjectsResult>(
                $"OBJECT_STORAGE_CONCERN_UNKNOWN: '{cmd.Concern}' is not a storage concern of this "
                + $"host. Known concerns: {ObjectStorageConcerns.Names}.");

        var destination = services.GetRequiredKeyedService<IObjectBackend>(concern.Name);

        // Refused rather than run as a no-op. With no bucket configured the destination IS the
        // filesystem the source is on, so every object would be found "already present" and the
        // report would say the migration succeeded — the most misleading possible answer to
        // "have my documents moved?".
        if (destination is LocalObjectBackend local)
            return Result.Fail<MigrateObjectsResult>(
                "OBJECT_STORAGE_NOT_CONFIGURED: there is no bucket to migrate to — "
                + $"'{concern.Name}' is still served from the filesystem at '{local.BasePath}'. "
                + "Set ObjectStorage:R2 and redeploy first.");

        var sourceBasePath = concern.ResolveLocalRoot(config, environment);

        // Nothing to copy is reported, not enqueued. An empty folder is the normal state of a
        // fresh install, and a job that finds nothing logs a clean report an operator would read
        // as "migrated" — the same confusion as above, one layer further away.
        if (!Directory.Exists(sourceBasePath))
            return Result.Fail<MigrateObjectsResult>(
                $"OBJECT_STORAGE_SOURCE_EMPTY: '{sourceBasePath}' does not exist, so '{concern.Name}' "
                + "has nothing on the filesystem to migrate.");

        var jobId = backgroundJobs.Enqueue<MigrateObjectsJob>(
            job => job.ExecuteAsync(concern.Name, sourceBasePath, currentUser.TenantId, currentUser.Id));

        logger.LogInformation(
            "Object-storage migration of {Concern} from {SourceBasePath} queued as job {JobId} by {RequestedBy}",
            concern.Name, sourceBasePath, jobId, currentUser.Id);

        return Result.Ok(new MigrateObjectsResult(
            Concern: concern.Name,
            JobId: jobId,
            SourceBasePath: sourceBasePath,
            Destination: destination.GetType().Name));
    }
}
