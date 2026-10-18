using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Sankore.Shared.Infrastructure.Auth;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sankore.Admin.Infrastructure.OpenApi;

internal static class SwaggerSetup
{
    public const string BearerScheme = "Bearer";
    public const string ApiKeyScheme = "ApiKey";

    public static IServiceCollection AddAdminSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.CustomSchemaIds(SchemaId);

            options.SwaggerDoc("v1", new()
            {
                Title = "Sankore Admin API",
                Version = "v1",
                Description = "Administration API for SankoreCRM"
            });

            options.AddSecurityDefinition(BearerScheme, new()
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Platform administrator token from POST /api/v1/auth/login."
            });

            options.AddSecurityDefinition(ApiKeyScheme, new()
            {
                Name = ApiKeyValidator.HeaderName,
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Service key used by Sankore.Api. Never sent by a browser."
            });

            options.OperationFilter<SecurityRequirementFilter>();
        });
        return services;
    }

    /// <summary>
    /// The type name, with the declaring type's name (minus "Endpoint") prefixed for nested types.
    ///
    /// <para>
    /// It used to be <c>type.FullName</c>, which is unique but makes the generated Angular client
    /// export interfaces named after namespaces. The short name alone is not enough: every
    /// endpoint here declares a nested <c>Request</c>, and Swashbuckle throws on the duplicate —
    /// so <c>CreateTenantEndpoint+Request</c> becomes <c>CreateTenantRequest</c>.
    /// </para>
    /// </summary>
    private static string SchemaId(Type type)
    {
        var name = type.IsGenericType
            ? type.Name[..type.Name.IndexOf('`')] + string.Concat(type.GetGenericArguments().Select(SchemaId))
            : type.Name;

        if (type.DeclaringType is not { } declaring)
            return name;

        var prefix = declaring.Name.EndsWith("Endpoint", StringComparison.Ordinal)
            ? declaring.Name[..^"Endpoint".Length]
            : declaring.Name;

        return name.StartsWith(prefix, StringComparison.Ordinal) ? name : prefix + name;
    }

    /// <summary>
    /// Declares, per operation, the credential that route really accepts — the document used to
    /// claim a bearer token everywhere while the host only ever checked the API key, so a
    /// generated client sent the wrong header. Anonymous routes declare none, the auth routes the
    /// bearer, and the registry routes either one (two requirement objects = OR in OpenAPI).
    /// </summary>
    private sealed class SecurityRequirementFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any())
                return;

            operation.Security = metadata.OfType<IAuthorizeData>().Any()
                ? [Requirement(BearerScheme)]
                : [Requirement(ApiKeyScheme), Requirement(BearerScheme)];
        }

        private static OpenApiSecurityRequirement Requirement(string scheme) => new()
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = scheme }
            }] = []
        };
    }
}
