namespace Sankore.Modules.Kyc.Features.Duplicates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder plus
/// a single line here — the repo's vertical-slice rule.
///
/// Mounted under <c>kyc-files</c> because a duplicate suspicion is a property of one file; the
/// detector itself has no endpoint at all, it runs inside the verification.
/// </summary>
internal static class KycDuplicatesEndpoints
{
    internal static IEndpointRouteBuilder MapKycDuplicatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC")
            .RequireKycInForce(); // read-only while the institution's KYC is off (docs/kyc-par-institution.md)

        group.MapClearDuplicateFlag();

        return app;
    }
}
