namespace Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class SetKycRemotePolicyEndpoint
{
    public static IEndpointRouteBuilder MapSetKycRemotePolicy(this IEndpointRouteBuilder app)
    {
        // PUT and not PATCH: the payload is the complete policy — see the command for why.
        app.MapPut("{productId:guid}/kyc-remote-policy", Handle)
            .WithName("SetProductKycRemotePolicy")
            .WithSummary("Configure remote KYC collection and validation for a product line")
            .WithDescription(
                "Decides whether converting a lead on this line sends the new customer a "
                + "single-use collection link, and whether the resulting file is validated by an "
                + "agent or by the machine. Automatic validation is refused unless the tenant has "
                + "enabled it (KYC_AUTO_APPROVAL_ENABLED) and the required tier stays within "
                + "KYC_AUTO_APPROVAL_MAX_TIER — see the compliance settings endpoint. Requires "
                + "permission: product:update.")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Accepts<SetKycRemotePolicyRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid productId, SetKycRemotePolicyRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SetKycRemotePolicyCommand(
            ProductId:            productId,
            Enabled:              req.Enabled,
            ValidationMode:       req.ValidationMode,
            RequiredTier:         req.RequiredTier,
            MinAutoScore:         req.MinAutoScore,
            RequiredDocuments:    req.RequiredDocuments,
            AllowedDocumentTypes: req.AllowedDocumentTypes,
            LinkValidityHours:    req.LinkValidityHours), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "PRODUCT_NOT_FOUND" => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error })
            };
    }
}

/// <summary>
/// The complete remote-KYC policy of a product line. Every field is replaced.
/// </summary>
/// <param name="ValidationMode">"Manual" (default) or "Automatic".</param>
/// <param name="RequiredTier">"Simplified" | "Full", or null.</param>
/// <param name="RequiredDocuments">
/// <c>KycDocumentKind</c> names: IdentityDocumentFront, IdentityDocumentBack, Selfie,
/// ProofOfAddress.
/// </param>
/// <param name="AllowedDocumentTypes">
/// <c>KycDocumentType</c> names: Cni, Passport, Cedeao, Consulaire. Required when
/// <paramref name="Enabled"/> is true.
/// </param>
public sealed record SetKycRemotePolicyRequest(
    bool Enabled = false,
    string? ValidationMode = null,
    string? RequiredTier = null,
    int MinAutoScore = 85,
    IReadOnlyList<string>? RequiredDocuments = null,
    IReadOnlyList<string>? AllowedDocumentTypes = null,
    int LinkValidityHours = 72);
