namespace Sankore.Api.Infrastructure;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// Publishes the Integration module's polymorphic <see cref="ConnectionSettings"/> as
/// <c>oneOf</c> + a <c>$kind</c> discriminator. See
/// <see cref="PolymorphicSettingsSchemaFilter{TBase}"/> for why reflection alone is not enough and
/// what the undocumented discriminator cost.
///
/// <para>
/// <see cref="ConnectionSettingsJsonConverter"/> refuses to infer a missing <c>$kind</c> — several
/// kinds share <c>BatchCapableSettings</c> and would be indistinguishable by their properties, so
/// a guess would store Amplitude coordinates on a Perfect Vision connection. The document must
/// therefore carry the discriminator: without it, a correctly filled settings object could only
/// come back 422.
/// </para>
/// </summary>
public sealed class ConnectionSettingsSchemaFilter
    : PolymorphicSettingsSchemaFilter<ConnectionSettings>
{
    protected override string Discriminator => "$kind";

    protected override string AbstractDiscriminatorProperty => "expectedKind";

    protected override IReadOnlyDictionary<string, Type> SubtypesByDiscriminator
        => ConnectionSettingsJsonConverter.SettingsTypesByKind;

    protected override string BaseDescription =>
        "Per-kind connection coordinates. The \"$kind\" discriminator is REQUIRED and names the "
        + "connection kind; it is matched case-insensitively and must agree with the row's own "
        + "\"kind\". Never carries a credential — only a vault reference.";

    protected override string ReadDiscriminator(ConnectionSettings instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.ExpectedKind.ToString();
    }
}
