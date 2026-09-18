using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// Replaces the default integer enum schema with a string schema that lists
/// every member name as a valid value. This makes Swagger UI show the human-
/// readable names instead of raw numeric constants.
/// </summary>
public sealed class EnumSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (!context.Type.IsEnum) return;

        schema.Type   = "string";
        schema.Format = null;
        schema.Enum.Clear();

        foreach (var name in Enum.GetNames(context.Type))
            schema.Enum.Add(new OpenApiString(name));

        // Surface the member list in the description so it is readable even in
        // plain-text tool-tips (Scalar, Redoc, generated SDK docs, etc.).
        var values = string.Join(", ", Enum.GetNames(context.Type));
        schema.Description = string.IsNullOrWhiteSpace(schema.Description)
            ? $"One of: {values}"
            : $"{schema.Description} — One of: {values}";
    }
}
