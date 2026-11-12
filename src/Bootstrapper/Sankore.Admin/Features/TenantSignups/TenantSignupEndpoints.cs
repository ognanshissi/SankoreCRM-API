namespace Sankore.Admin.Features.TenantSignups;

/// <summary>
/// Free-trial requests from the Talisoft website (<c>docs/essai-gratuit-socle.md</c>, lot 1). One
/// group, two audiences:
/// <list type="bullet">
/// <item>the service key (Sankore.Api, which relays the website): the public routes of
/// <see cref="TrialPublicEndpoints"/>, the registry's own availability, creation and confirmation, and
/// the feed of decisions still to mail;</item>
/// <item>the platform administrators (<c>RequirePlatformAdmin</c>): the queue, a request's detail,
/// approval and refusal.</item>
/// </list>
/// </summary>
public static class TenantSignupEndpoints
{
    public static void MapTenantSignupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tenant-signups").WithTags("Tenant signups");

        TrialPublicEndpoints.Map(group);
        TrialSlugAvailabilityEndpoint.Map(group);
        CreateTenantSignupEndpoint.Map(group);
        ConfirmTenantSignupEndpoint.Map(group);
        TenantSignupDecisionFeedEndpoints.Map(group);

        ListTenantSignupsEndpoint.Map(group);
        GetTenantSignupEndpoint.Map(group);
        DecideTenantSignupEndpoints.Map(group);
    }
}
