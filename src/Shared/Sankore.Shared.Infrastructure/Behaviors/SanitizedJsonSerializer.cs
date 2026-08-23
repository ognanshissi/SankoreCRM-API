namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sankore.Shared.Kernel;

/// <summary>
/// Wrapper around <see cref="JsonSerializer"/> that redacts any property
/// decorated with <see cref="SensitiveDataAttribute"/> before serialization.
///
/// Redacted properties are replaced by the literal string <c>"***"</c>.
/// The resolved <see cref="JsonSerializerOptions"/> instance is cached
/// (thread-safe after first creation) so reflection happens once per type,
/// not once per command.
/// </summary>
public static class SanitizedJsonSerializer
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions();
        opts.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { RedactSensitiveProperties }
        };
        return opts;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to JSON with all
    /// <see cref="SensitiveDataAttribute"/>-marked properties replaced by <c>"***"</c>.
    /// </summary>
    public static string Serialize(object value)
        => JsonSerializer.Serialize(value, value.GetType(), Options);

    private static void RedactSensitiveProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;

        foreach (var prop in info.Properties)
        {
            var isSensitive = prop.AttributeProvider?
                .GetCustomAttributes(typeof(SensitiveDataAttribute), inherit: true)
                .Length > 0;

            if (isSensitive)
                prop.Get = _ => "***";
        }
    }
}
