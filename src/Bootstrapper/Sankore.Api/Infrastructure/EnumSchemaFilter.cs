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
        // Unwrap Nullable<T> before the test. typeof(LeadGender?).IsEnum is FALSE — a nullable
        // enum is a struct wrapping one — so guarding on context.Type.IsEnum silently skipped
        // every OPTIONAL enum in the API while rewriting every required one. The contract came
        // out split: LeadSourceListDto.status read back as "Active" while the filter that
        // selects it, LeadSourceStatus? on the query, was documented as integer 0..5. That hit
        // 54 places (27 query parameters, 27 schema properties), and the generated TypeScript
        // client turned each of them into a numeric enum even though it is configured for
        // string enums — the magic numbers the front had to pass came from here.
        var enumType = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (!enumType.IsEnum) return;

        schema.Type   = "string";
        schema.Format = null;
        schema.Enum.Clear();

        foreach (var name in Enum.GetNames(enumType))
            schema.Enum.Add(new OpenApiString(name));

        // Surface the member list in the description so it is readable even in
        // plain-text tool-tips (Scalar, Redoc, generated SDK docs, etc.).
        var values = string.Join(", ", Enum.GetNames(enumType));
        schema.Description = string.IsNullOrWhiteSpace(schema.Description)
            ? $"One of: {values}"
            : $"{schema.Description} — One of: {values}";
    }
}
