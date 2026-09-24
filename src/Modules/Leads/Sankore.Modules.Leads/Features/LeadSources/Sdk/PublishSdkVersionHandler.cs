namespace Sankore.Modules.Leads.Features.LeadSources.Sdk;

using System.Security.Cryptography;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class PublishSdkVersionHandler(
    LeadsDbContext db,
    ISdkFileStore fileStore)
    : IRequestHandler<PublishSdkVersionCommand, Result<PublishSdkVersionResult>>
{
    public async Task<Result<PublishSdkVersionResult>> Handle(
        PublishSdkVersionCommand cmd, CancellationToken ct)
    {
        // Check version uniqueness
        var exists = await db.SdkVersions
            .IgnoreQueryFilters()
            .AnyAsync(v => v.Version == cmd.Version, ct);

        if (exists)
            return Result.Fail<PublishSdkVersionResult>("SDK_VERSION_ALREADY_EXISTS");

        // Compute SHA-384 SRI hash
        var hashBytes = SHA384.HashData(cmd.FileContent);
        var sriHash = $"sha384-{Convert.ToBase64String(hashBytes)}";

        // Write file to store
        await fileStore.WriteAsync(cmd.Version, "forms.min.js", cmd.FileContent, ct);

        // Revoke previous current for this major
        var previousCurrent = await db.SdkVersions
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(v => v.Major == cmd.Major && v.IsCurrent)
            .ToListAsync(ct);

        foreach (var prev in previousCurrent)
            prev.Revoke();

        // Create new version
        var version = SdkVersion.Publish(cmd.Version, cmd.Major, sriHash);
        db.SdkVersions.Add(version);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new PublishSdkVersionResult(version.Id, version.Version, sriHash));
    }
}
