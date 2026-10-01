namespace Sankore.Modules.Kyc.Features.Files;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Files.CreateKycFile;
using Sankore.Modules.Kyc.Features.Files.GetKycFile;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
/// </summary>
internal static class KycFilesEndpoints
{
    internal static IEndpointRouteBuilder MapKycFilesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapCreateKycFile();
        group.MapGetKycFile();

        return app;
    }
}
