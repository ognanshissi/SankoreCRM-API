namespace Sankore.Api.Features.ObjectStorage.MigrateObjects;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Copies one concern's objects from the filesystem root this host would otherwise use into the
/// configured bucket. Platform operation, run once when an installation adopts object storage.
///
/// <para>
/// <b>It names a concern and nothing else — in particular, no path.</b> An earlier shape took the
/// source directory as a parameter, which would have made this endpoint an authenticated "read any
/// folder on the server into a bucket we can download from". The source is resolved from
/// configuration, from the same entry start-up used, so the only thing a caller can choose is
/// which of two known concerns to move.
/// </para>
///
/// <para>
/// <b>There is no delete-source option, deliberately.</b> The migrator supports one; it is not
/// exposed here and must not be. The objects behind <c>kyc-documents</c> are identity documents a
/// regulator can demand, and no HTTP request should be able to destroy the last copy of one. The
/// source is retired by unmounting the volume — an ops action, reversible until it is taken, and
/// taken only after a document has been read back through the API.
/// </para>
/// </summary>
internal sealed record MigrateObjectsCommand(string Concern)
    : IRequest<Result<MigrateObjectsResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ObjectStorageMigration";

    /// <summary>The concern IS the resource: one bucket, one migration, one audit row to find later.</summary>
    public string ResourceId => Concern;
}

/// <param name="JobId">
/// The Hangfire job. Surfaced so an operator can watch it in the dashboard — the copy may run for
/// hours and the HTTP request is long gone by then.
/// </param>
/// <param name="SourceBasePath">
/// Echoed back so the operator can confirm the server resolved the folder they meant, before the
/// job has read a single object.
/// </param>
internal sealed record MigrateObjectsResult(
    string Concern, string JobId, string SourceBasePath, string Destination);
