using Microsoft.AspNetCore.Builder;

namespace Sankore.Shared.Infrastructure.Extensions;

/// <summary>Marker added by <see cref="EndpointBuilderExtensions.WithTenantHeader"/>.</summary>
public sealed class TenantHeaderMetadata;

public static class EndpointBuilderExtensions
{
    /// <summary>
    /// Marks the endpoint so that the <c>x-tenant-id</c> header appears in Swagger.
    /// The Bootstrapper's <c>TenantHeaderOperationFilter</c> converts this marker
    /// into an OpenAPI parameter — keeping OpenAPI types out of Shared.Infrastructure.
    /// </summary>
    public static RouteHandlerBuilder WithTenantHeader(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new TenantHeaderMetadata());
}
