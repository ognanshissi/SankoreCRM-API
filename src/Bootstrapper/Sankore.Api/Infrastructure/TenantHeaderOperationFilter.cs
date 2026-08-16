using Microsoft.OpenApi.Models;
using Sankore.Shared.Infrastructure.Extensions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// Swashbuckle operation filter: when an endpoint is decorated with
/// <see cref="EndpointBuilderExtensions.WithTenantHeader"/>, adds the
/// <c>x-tenant-id</c> header parameter to the generated OpenAPI operation.
/// </summary>
public sealed class TenantHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasTenantHeader = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<TenantHeaderMetadata>()
            .Any();

        if (!hasTenantHeader) return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "x-tenant-id",
            In = ParameterLocation.Header,
            Required = false,
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
            Description = "Tenant identifier (UUID). Overrides the tenant_id JWT claim when present."
        });
    }
}
