namespace Sankore.Modules.Kyc.Features.Files;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Files.CreateKycFile;
using Sankore.Modules.Kyc.Features.Files.GetKycFile;
using Sankore.Modules.Kyc.Features.Files.ListKycFiles;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
/// </summary>
internal static class KycFilesEndpoints
{
    internal static IEndpointRouteBuilder MapKycFilesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC")
            .RequireKycInForce(); // read-only while the institution's KYC is off (docs/kyc-par-institution.md)

        group.MapCreateKycFile();
        group.MapListKycFiles();
        group.MapGetKycFile();

        return app;
    }
}
