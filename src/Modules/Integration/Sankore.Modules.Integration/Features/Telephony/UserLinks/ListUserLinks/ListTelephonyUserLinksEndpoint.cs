namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.ListUserLinks;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListTelephonyUserLinksEndpoint
{
    internal static IEndpointRouteBuilder MapListTelephonyUserLinks(this IEndpointRouteBuilder app)
    {
        app.MapGet("user-links", Handle)
            .WithName("ListTelephonyUserLinks")
            .WithSummary("Which CRM users are linked to a provider account")
            .WithDescription(
                "Broken links first — those are the ones asking for action, and statusReason "
                + "says what the nightly check found. Disabled links are excluded unless "
                + "includeDisabled=true: they are history, kept because they explain calls "
                + "already in the journal. Reads this module's own rows and never calls the "
                + "provider, so it answers normally while the provider is down. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces<IReadOnlyList<TelephonyUserLinkDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, CancellationToken ct, bool includeDisabled = false)
        => Results.Ok((await sender.Send(
            new ListTelephonyUserLinksQuery(includeDisabled), ct)).Value);
}
