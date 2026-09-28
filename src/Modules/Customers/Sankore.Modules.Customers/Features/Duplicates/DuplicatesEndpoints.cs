namespace Sankore.Modules.Customers.Features.Duplicates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;
using Sankore.Modules.Customers.Features.Duplicates.ListDuplicateCandidates;
using Sankore.Modules.Customers.Features.Duplicates.Merge.ApproveClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.ListClientMerges;
using Sankore.Modules.Customers.Features.Duplicates.Merge.RejectClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;
using Sankore.Modules.Customers.Features.Duplicates.RejectDuplicateCandidate;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Endpoint aggregator of the deduplication zone (US-M01-BE-23/24/25). Two route roots, because the
/// review queue and the merge workflow are two different resources:
/// <list type="bullet">
///   <item><description><c>clients/duplicates</c> — the candidates a human has to arbitrate.</description></item>
///   <item><description><c>clients/merges</c> — the four-eyes merge requests.</description></item>
/// </list>
/// Both sit under <c>api/v1</c>. <c>clients/duplicates</c> cannot collide with the Clients zone's
/// <c>clients/{clientId:guid}</c>: the literal never matches the <c>:guid</c> constraint.
/// </summary>
public static class DuplicatesEndpoints
{
    public static IEndpointRouteBuilder MapDuplicatesEndpoints(this IEndpointRouteBuilder app)
    {
        var duplicates = app.MapGroup("clients/duplicates").WithTags("Client duplicates");
        duplicates
            .MapListDuplicateCandidates()
            .MapRejectDuplicateCandidate()
            .MapBackfillPhoneticKeys();

        var merges = app.MapGroup("clients/merges").WithTags("Client merges");
        merges
            .MapRequestClientMerge()
            .MapListClientMerges()
            .MapGetClientMerge()
            .MapApproveClientMerge()
            .MapRejectClientMerge();

        return app;
    }
}
