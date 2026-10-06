namespace Sankore.Modules.Kyc.Features.Documents;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Documents.GetIdentityDocument;
using Sankore.Modules.Kyc.Features.Documents.ListDocuments;
using Sankore.Modules.Kyc.Features.Documents.ReadDocument;
using Sankore.Modules.Kyc.Features.Documents.ReviewDocument;
using Sankore.Modules.Kyc.Features.Documents.UploadDocument;

internal static class KycDocumentsEndpoints
{
    internal static IEndpointRouteBuilder MapKycDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapUploadKycDocument();
        group.MapReadKycDocument();
        group.MapGetKycIdentityDocument();
        group.MapListKycDocuments();
        group.MapReviewKycDocument();

        return app;
    }
}
