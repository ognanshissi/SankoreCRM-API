namespace Sankore.Api.Infrastructure;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Publishes M13's polymorphic <see cref="SourceSettings"/> as <c>oneOf</c> + a <c>$mode</c>
/// discriminator. See <see cref="PolymorphicSettingsSchemaFilter{TBase}"/> for why reflection
/// alone is not enough.
///
/// <para>
/// This hierarchy went undocumented longer than M14's because it fails SILENTLY rather than with a
/// 422: <see cref="SourceSettingsJsonConverter"/> infers a missing <c>$mode</c> from the payload's
/// shape and falls back to <c>Internal</c>. A ScheduledPull body that happens not to carry
/// <c>endpointUrl</c>, <c>cronSchedule</c> or <c>authType</c> is therefore deserialized as
/// <c>InternalSettings</c>, and every pull field is dropped on save with no error anywhere. The
/// front end built a hand-written discriminated union and a <c>camelizeKeys</c> normaliser around
/// exactly this gap (<c>lead-source-settings.types.ts</c> says so in its header); publishing the
/// real shape is what lets that compensation layer eventually go.
/// </para>
///
/// <para>
/// The inference stays on the server — removing it would break callers that rely on it, including
/// the SDK — but the document declares <c>$mode</c> required, so no generated client depends on
/// the guess.
/// </para>
/// </summary>
public sealed class SourceSettingsSchemaFilter : PolymorphicSettingsSchemaFilter<SourceSettings>
{
    protected override string Discriminator => "$mode";

    protected override string AbstractDiscriminatorProperty => "expectedMode";

    protected override IReadOnlyDictionary<string, Type> SubtypesByDiscriminator
        => SourceSettingsJsonConverter.SettingsTypesByMode;

    protected override string BaseDescription =>
        "Mode-specific lead-source settings. The \"$mode\" discriminator names the integration "
        + "mode and must agree with the source's own \"mode\". ALWAYS send it: the server infers "
        + "it from the payload's shape when absent and falls back to Internal, which silently "
        + "drops every mode-specific field. Never carries a credential — only a vault reference.";

    protected override string ReadDiscriminator(SourceSettings instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.ExpectedMode.ToString();
    }
}
