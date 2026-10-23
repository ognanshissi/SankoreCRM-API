using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sankore.Admin.Infrastructure.OpenApi;

/// <summary>
/// Documents enums as the strings this host actually writes (<c>JsonStringEnumConverter</c> in
/// Program.cs). Swashbuckle does not see that converter on minimal APIs and documented them as
/// integers, so the generated admin client turned <c>ModuleSubscriptionStatus</c> into
/// <c>NUMBER_0…NUMBER_4</c> for a field that arrives as <c>"Requested"</c>. Twin of
/// Sankore.Api's filter of the same name; Shared.Infrastructure has no Swashbuckle reference.
/// </summary>
internal sealed class EnumSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        // Nullable<T> first: typeof(ModuleSubscriptionStatus?).IsEnum is false.
        var enumType = Nullable.GetUnderlyingType(context.Type) ?? context.Type;
        if (!enumType.IsEnum) return;

        schema.Type = "string";
        schema.Format = null;
        schema.Enum.Clear();
        foreach (var name in Enum.GetNames(enumType))
            schema.Enum.Add(new OpenApiString(name));
    }
}
