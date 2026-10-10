namespace Sankore.Api.Tests.Infrastructure;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Sankore.Api.Infrastructure;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;
using ConnectionSettings = Sankore.Modules.Integration.Domain.ConnectionSettings;
using IntegrationKind = Sankore.Modules.Integration.PublicApi.IntegrationKind;
using LeadIntegrationMode = Sankore.Modules.Leads.Domain.IntegrationMode;
using SourceSettings = Sankore.Modules.Leads.Domain.SourceSettings;

/// <summary>
/// The published shape of the two polymorphic settings hierarchies.
///
/// <para>
/// <b>Why this suite exists.</b> <c>ConnectionSettingsJsonConverterTests</c> and
/// <c>SourceSettingsConverterTests</c> pin what a client may SEND; nothing pinned what the
/// contract ADVERTISES, and the two had diverged completely. Reflection over an abstract record
/// documented the base only, so the discriminator each converter resolves through appeared nowhere
/// while <c>expectedKind</c> / <c>expectedMode</c> — the one field a client must not send — was
/// published as readOnly. M14 surfaced that as a 422 on correctly filled settings; M13 surfaced it
/// as nothing at all, inferring a mode and silently dropping every field of the one it guessed
/// wrong. A test over a converter cannot see either: this is about the document.
/// </para>
///
/// <para>
/// Both halves are driven off their enum, like <c>ConnectionSettingsJsonConverterTests</c>, so a
/// new kind or mode fails here until it is documented rather than reaching a tenant as a 422 they
/// cannot act on — or as a field that does not save.
/// </para>
/// </summary>
public abstract class PolymorphicSettingsSchemaFilterTests
{
    /// <summary>The abstract base whose schema the filter rewrites.</summary>
    protected abstract Type BaseType { get; }

    protected abstract ISchemaFilter Filter { get; }

    /// <summary>Wire name of the discriminator.</summary>
    protected abstract string Discriminator { get; }

    /// <summary>Its camelCase C# counterpart, which must NOT be published.</summary>
    protected abstract string AbstractProperty { get; }

    /// <summary>Every discriminator value the enum admits.</summary>
    protected abstract IReadOnlyCollection<string> EveryDiscriminatorValue { get; }

    /// <summary>The table the converter resolves through, keyed by discriminator value.</summary>
    protected abstract IReadOnlyDictionary<string, Type> ConverterTable { get; }

    private SchemaRepository? repository;

    /// <summary>
    /// Generates against the same filters and naming policy <c>Program.cs</c> configures: the
    /// abstract property is looked up by its camelCase name, so a generator built with the default
    /// policy would pass while the real document kept <c>ExpectedKind</c>.
    /// </summary>
    private SchemaRepository Repository
    {
        get
        {
            if (this.repository is not null) return this.repository;

            var options = new SchemaGeneratorOptions { UseInlineDefinitionsForEnums = true };
            options.SchemaFilters.Add(new EnumSchemaFilter());
            options.SchemaFilters.Add(this.Filter);

            var generator = new SchemaGenerator(
                options,
                new JsonSerializerDataContractResolver(
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            this.repository = new SchemaRepository();
            generator.GenerateSchema(this.BaseType, this.repository);
            return this.repository;
        }
    }

    private OpenApiSchema BaseSchema => this.Repository.Schemas[this.BaseType.Name];

    [Fact]
    public void Base_schema_is_a_discriminated_union_and_not_an_object()
    {
        // Not merely "oneOf is populated": left as `type: object` with the inherited properties
        // still on it, the schema reads as "an object AND one of these" and NSwag emits the base's
        // own knobs twice — once flat, once per subtype.
        this.BaseSchema.Type.Should().BeNull();
        this.BaseSchema.Properties.Should().BeEmpty();
        this.BaseSchema.OneOf.Should().HaveCount(this.EveryDiscriminatorValue.Count);
        this.BaseSchema.Discriminator!.PropertyName.Should().Be(this.Discriminator);
    }

    [Fact]
    public void Every_enum_value_is_reachable_through_the_discriminator()
    {
        foreach (var value in this.EveryDiscriminatorValue)
        {
            this.BaseSchema.Discriminator!.Mapping.Should().ContainKey(
                value, "a client cannot name a {0} the document does not map", this.Discriminator);

            var id = this.BaseSchema.Discriminator.Mapping[value].Split('/')[^1];

            // The mapping and the oneOf list must point at the same schemas: a mapping entry
            // naming a schema absent from oneOf validates against nothing, and a oneOf member
            // absent from the mapping is unreachable by discriminator.
            this.BaseSchema.OneOf.Should().Contain(s => s.Reference != null && s.Reference.Id == id);
            this.Repository.Schemas.Should().ContainKey(id);
        }
    }

    [Fact]
    public void Every_subtype_declares_its_discriminator_as_required()
    {
        foreach (var value in this.EveryDiscriminatorValue)
        {
            var schema = this.SubtypeSchemaFor(value);

            // Required, not merely present: a discriminated union is resolved by reading the
            // property from the payload. M14's converter answers null without it; M13's GUESSES.
            // A document that lets it be optional publishes that guess as a supported call.
            schema.Required.Should().Contain(this.Discriminator);
            schema.Properties.Should().ContainKey(this.Discriminator);

            schema.Properties[this.Discriminator].Enum.Should().ContainSingle()
                .Which.Should().BeOfType<OpenApiString>()
                .Which.Value.Should().Be(value, "the discriminator is pinned per subtype");
        }
    }

    [Fact]
    public void Every_subtype_publishes_the_abstract_discriminator_property_as_readonly()
    {
        // ExpectedKind / ExpectedMode must be BOTH present and readOnly, and the pairing is the
        // point. Present, because both converters serialize the concrete record, so a response
        // really does carry it and the front really does display it — dropping it from the schema
        // broke the Angular build (TS2339) against a server that kept sending the field. ReadOnly,
        // because the converters ignore it on the way in: the discriminator is $kind / $mode.
        foreach (var value in this.EveryDiscriminatorValue)
        {
            var schema = this.SubtypeSchemaFor(value);

            schema.Properties.Should().ContainKey(this.AbstractProperty);
            schema.Properties[this.AbstractProperty].ReadOnly.Should().BeTrue();
            schema.Required.Should().NotContain(this.AbstractProperty);
        }
    }

    [Fact]
    public void Every_subtype_publishes_its_own_coordinates()
    {
        foreach (var value in this.EveryDiscriminatorValue)
        {
            var type = this.ConverterTable[value];
            var schema = this.SubtypeSchemaFor(value);

            // The failure this suite was written for: the base's knobs were published and every
            // per-kind field — baseUrl, entity, sftpHost, pull — was not. Counting against the
            // record's own declared properties keeps that honest as records gain fields.
            //
            // [JsonIgnore] properties are excluded, and must be: M13's records expose the flat
            // names and folded shapes the server consumes (EndpointUrl composed from three
            // editor fields, AuthType folded from two) as get-only projections that never cross
            // the wire. Counting them would demand that the contract publish a second, derived
            // way to say everything — the very duplication the projections exist to avoid.
            var declared = type
                .GetProperties()
                .Count(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

            schema.Properties.Should().HaveCount(
                declared + 1,
                "every property of {0}, plus {1}, must be documented",
                type.Name,
                this.Discriminator);
        }
    }

    [Fact]
    public void The_schema_filter_and_the_converter_agree_on_every_pairing()
    {
        // The filter reads the discriminator off an INSTANCE rather than reversing the table, so a
        // table entry paired with the wrong record publishes a schema whose own $kind contradicts
        // the mapping that reaches it. Nothing in the document would look wrong; a client would
        // simply be told to send "ServerWebhook" and have it stored as something else.
        foreach (var value in this.EveryDiscriminatorValue)
        {
            var mapped = this.BaseSchema.Discriminator!.Mapping[value].Split('/')[^1];

            mapped.Should().Be(
                this.ConverterTable[value].Name,
                "the document must route {0} \"{1}\" to the record the converter resolves it to",
                this.Discriminator,
                value);

            this.SubtypeSchemaFor(value).Properties[this.Discriminator].Enum
                .Should().ContainSingle()
                .Which.As<OpenApiString>().Value.Should().Be(
                    value, "{0}'s own ExpectedKind/ExpectedMode must match its table key", mapped);
        }
    }

    private OpenApiSchema SubtypeSchemaFor(string discriminatorValue)
    {
        var id = this.BaseSchema.Discriminator!.Mapping[discriminatorValue].Split('/')[^1];
        return this.Repository.Schemas[id];
    }

    /// <summary>M14 — <c>$kind</c>, whose converter refuses to infer a missing discriminator.</summary>
    public sealed class ConnectionSettingsSchema : PolymorphicSettingsSchemaFilterTests
    {
        protected override Type BaseType => typeof(ConnectionSettings);

        protected override ISchemaFilter Filter => new ConnectionSettingsSchemaFilter();

        protected override string Discriminator => "$kind";

        protected override string AbstractProperty => "expectedKind";

        protected override IReadOnlyCollection<string> EveryDiscriminatorValue
            => Enum.GetNames<IntegrationKind>();

        protected override IReadOnlyDictionary<string, Type> ConverterTable
            => ConnectionSettingsJsonConverter.SettingsTypesByKind;
    }

    /// <summary>M13 — <c>$mode</c>, whose converter infers one and falls back to Internal.</summary>
    public sealed class SourceSettingsSchema : PolymorphicSettingsSchemaFilterTests
    {
        protected override Type BaseType => typeof(SourceSettings);

        protected override ISchemaFilter Filter => new SourceSettingsSchemaFilter();

        protected override string Discriminator => "$mode";

        protected override string AbstractProperty => "expectedMode";

        protected override IReadOnlyCollection<string> EveryDiscriminatorValue
            => Enum.GetNames<LeadIntegrationMode>();

        protected override IReadOnlyDictionary<string, Type> ConverterTable
            => SourceSettingsJsonConverter.SettingsTypesByMode;
    }
}
