namespace Sankore.Api.Infrastructure;

using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Publishes a settings hierarchy that a hand-written <c>JsonConverter</c> resolves through a
/// discriminator as <c>oneOf</c> + an OpenAPI discriminator, so the contract describes the body
/// the converter actually requires.
///
/// <para>
/// <b>Why this exists.</b> Both polymorphic payloads in this API — M13's <c>SourceSettings</c>
/// (<c>$mode</c>) and M14's <c>ConnectionSettings</c> (<c>$kind</c>) — are deserialized by a
/// converter in this assembly rather than by <c>[JsonPolymorphic]</c>, because the records are
/// plain and the host owns the serializer. Swashbuckle cannot see any of that: reflecting over an
/// abstract record it documents the BASE and stops. The emitted schema carried
/// <c>schemaVersion</c> and the base's own knobs, plus <c>expectedKind</c> / <c>expectedMode</c>
/// as <c>readOnly</c> — which is the one field a client must not send — and neither the
/// discriminator nor a single subtype property. So every generated client and every Swagger "Try
/// it out" could produce only a body the converter resolves to <c>null</c> or, worse, GUESSES.
/// </para>
///
/// <para>
/// The two failure shapes are different and both are real. M14's converter refuses to infer a
/// missing discriminator, so an undocumented <c>$kind</c> surfaced as a 422 — "settings is
/// required and must carry a \"$kind\"" — on settings the caller had filled in correctly. M13's
/// infers <c>$mode</c> from the payload's shape and falls back to <c>Internal</c>, so an
/// undocumented <c>$mode</c> surfaced as nothing at all: a ScheduledPull body missing the
/// properties <c>InferMode</c> looks for is deserialized as <c>InternalSettings</c> and every pull
/// field is dropped on save. A document that names the discriminator is what removes the guess.
/// </para>
///
/// <para>
/// <b>Subtypes come from the converter's own table</b>, never from a second list here: the
/// discriminator a client may SEND and the one the document ADVERTISES have to be the same set,
/// and the drift between them is precisely the bug above.
/// </para>
/// </summary>
/// <typeparam name="TBase">
/// The abstract settings base. Every concrete subtype must be instantiable with no arguments —
/// they are records with init-only properties, and <see cref="ReadDiscriminator"/> reads the
/// discriminator off an instance rather than reversing the table, so a table entry paired with the
/// wrong record cannot quietly mislabel a schema.
/// </typeparam>
public abstract class PolymorphicSettingsSchemaFilter<TBase> : ISchemaFilter
    where TBase : class
{
    /// <summary>The wire name of the discriminator, e.g. <c>$kind</c>.</summary>
    protected abstract string Discriminator { get; }

    /// <summary>
    /// The abstract C# property behind the discriminator, under the serializer's camelCase policy
    /// (e.g. <c>expectedKind</c>).
    ///
    /// <para>
    /// KEPT in the document, and pinned <c>readOnly</c>. It is tempting to strip it as "the
    /// mechanism, not a field of the wire format", but that is false on the facts: both converters
    /// serialize the concrete record, so a response carries the property, and the front end
    /// displays it (<c>connection-detail.html</c> binds <c>dto.settings?.expectedKind</c>).
    /// Removing it from the schema while the server still sends it produced
    /// <c>TS2339: Property 'expectedKind' does not exist on type 'ConnectionSettings'</c> and a
    /// schema promising <c>additionalProperties: false</c> against a body that carries one.
    /// <c>readOnly</c> is the honest statement: present in a response, never accepted in a
    /// request — which is exactly how the converters treat it.
    /// </para>
    /// </summary>
    protected abstract string AbstractDiscriminatorProperty { get; }

    /// <summary>Discriminator value to concrete record, as the converter resolves it.</summary>
    protected abstract IReadOnlyDictionary<string, Type> SubtypesByDiscriminator { get; }

    /// <summary>Prose for the base schema; the only per-module copy in this filter.</summary>
    protected abstract string BaseDescription { get; }

    /// <summary>The discriminator value this instance claims — its own answer, not a lookup.</summary>
    protected abstract string ReadDiscriminator(TBase instance);

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Type == typeof(TBase))
        {
            ApplyToBase(schema, context);
            return;
        }

        // Reached through the GenerateSchema calls below, and also on its own if a subtype is ever
        // referenced directly from an endpoint: either way the discriminator must be declared.
        if (typeof(TBase).IsAssignableFrom(context.Type) && !context.Type.IsAbstract)
            ApplyToSubtype(schema, context.Type);
    }

    private void ApplyToBase(OpenApiSchema schema, SchemaFilterContext context)
    {
        // Cleared, not kept alongside oneOf: left as `type: object` with the inherited properties
        // still on it, the schema reads as "an object AND one of these" and a generator that
        // honours it emits the base's own knobs twice — once flat, once per subtype.
        schema.Type = null;
        schema.Properties.Clear();
        schema.Required.Clear();
        schema.AdditionalPropertiesAllowed = true;
        schema.OneOf.Clear();

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (discriminator, type) in SubtypesByDiscriminator)
        {
            // Registers the definition in the repository and hands back a $ref to it.
            var reference = context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository);

            schema.OneOf.Add(reference);

            if (reference.Reference?.Id is { } id)
                mapping[discriminator] = $"#/components/schemas/{id}";
        }

        schema.Discriminator = new OpenApiDiscriminator
        {
            PropertyName = Discriminator,
            Mapping = mapping,
        };

        schema.Description = BaseDescription;
    }

    private void ApplyToSubtype(OpenApiSchema schema, Type type)
    {
        var value = ReadDiscriminator((TBase)Activator.CreateInstance(type)!);

        // Reasserted rather than assumed: Swashbuckle infers readOnly from a get-only property, so
        // this is already true — and it is the whole reason the property can stay in the document
        // without inviting a client to set the one the converter ignores.
        if (schema.Properties.TryGetValue(AbstractDiscriminatorProperty, out var abstractProperty))
            abstractProperty.ReadOnly = true;

        schema.Properties[Discriminator] = new OpenApiSchema
        {
            Type = "string",
            Enum = { new OpenApiString(value) },
            Description = $"Discriminator. Must be \"{value}\" for these settings.",
        };

        // REQUIRED in the document even where the converter tolerates its absence (M13 infers it).
        // OpenAPI's discriminator keyword wants the property required in every subschema, and a
        // client that always sends it never depends on a guess; the server is unchanged, so
        // callers that omit it keep working. Documenting it as optional would publish the
        // inference as a supported way to call the API.
        // Required is a set, so Add is already idempotent.
        schema.Required.Add(Discriminator);
    }
}
