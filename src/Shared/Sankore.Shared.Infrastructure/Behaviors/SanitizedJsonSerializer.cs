namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Sankore.Shared.Kernel;

/// <summary>
/// Wrapper around <see cref="JsonSerializer"/> that redacts any property
/// decorated with <see cref="SensitiveDataAttribute"/> before serialization.
///
/// Redacted properties keep their name and are given the literal value
/// <c>"***"</c>, so an audit entry still records WHICH sensitive fields a command
/// carried without ever recording their value.
///
/// Redaction is applied through a per-property <see cref="JsonConverter"/> rather
/// than by overwriting the property getter. Overwriting the getter only works for
/// <see cref="string"/> properties: System.Text.Json casts the getter's result back
/// to the property's declared type, so returning <c>"***"</c> for, say, a
/// <c>DateOnly?</c>, a <c>IReadOnlyList&lt;string&gt;</c> or a nested record threw
/// <see cref="InvalidCastException"/> at serialization time — which, since the audit
/// pipeline serializes every command, turned a correctly-annotated command into a
/// failing write. A converter has no such constraint and redacts any type.
///
/// The resolved <see cref="JsonSerializerOptions"/> instance is cached (thread-safe
/// after first creation) so reflection happens once per type, not once per command.
/// </summary>
public static class SanitizedJsonSerializer
{
    /// <summary>Value written in place of every sensitive property.</summary>
    public const string RedactedValue = "***";

    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static readonly ConcurrentDictionary<Type, JsonConverter> RedactingConverters = new();

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
    /// <see cref="SensitiveDataAttribute"/>-marked properties replaced by
    /// <see cref="RedactedValue"/>, whatever their declared type.
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
                prop.CustomConverter = RedactingConverterFor(prop.PropertyType);
        }
    }

    private static JsonConverter RedactingConverterFor(Type propertyType)
        => RedactingConverters.GetOrAdd(
            propertyType,
            static type => (JsonConverter)Activator.CreateInstance(
                typeof(RedactingConverter<>).MakeGenericType(type))!);

    /// <summary>
    /// Writes <see cref="RedactedValue"/> for any value of <typeparamref name="T"/>.
    /// Write-only: the audit payload is never deserialized back into a command.
    /// </summary>
    private sealed class RedactingConverter<T> : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(
                "A redacted audit payload is write-only and cannot be deserialized.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(RedactedValue);
    }
}
