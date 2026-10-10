namespace Sankore.Modules.Integration.Features.Snapshot;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// How the snapshot's two jsonb columns are written.
///
/// <para>
/// camelCase and enums-as-names, which is exactly what <c>IntegrationModuleFacade.SnapshotJson</c>
/// reads with. The two halves of this read model are written and read by different chantiers, and
/// a disagreement on the naming policy does not fail: the reader simply deserialises every
/// property to its default, so Customer 360 shows a complete-looking customer whose balances are
/// all zero, with nothing in the logs. The options are therefore stated here rather than inlined
/// at the call site, and a round-trip test pins them.
/// </para>
///
/// <para>
/// Enums as NAMES and not numbers for the same reason the command payloads are: the column is read
/// by a human during an incident, and renumbering an enum must not silently re-interpret rows
/// already written.
/// </para>
/// </summary>
internal static class SnapshotSerialization
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serialises a list for one of the jsonb columns. Never <c>null</c>: the column is
    /// <c>IsRequired</c>, and an absent list is an empty array rather than a null the reader would
    /// have to guard.
    /// </summary>
    internal static string Serialise<T>(IReadOnlyList<T> items)
        => JsonSerializer.Serialize(items, Options);
}
