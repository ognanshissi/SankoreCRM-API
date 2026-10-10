namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Stores <see cref="HoldingSpecifics"/> as a <c>jsonb</c> document (BNF-B-14).
///
/// <para>
/// <b>No <c>ValueComparer</c>, and that is a decision rather than an omission.</b> A converted
/// property normally needs one — M12's <c>BeneficiaryPolicyComparer</c> exists because record
/// equality does NOT compare list elements, so adding a role left the entity looking unchanged and
/// the write was silently dropped. <see cref="HoldingSpecifics"/> is flat: every member is a
/// scalar, so the record's own structural equality is correct, and a hand-written comparer would be
/// one more thing to keep in step with the record.
/// </para>
/// </summary>
internal sealed class HoldingSpecificsConverter()
    : ValueConverter<HoldingSpecifics, string>(
        specifics => Serialize(specifics),
        json => Deserialize(json))
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(HoldingSpecifics specifics)
        => JsonSerializer.Serialize(specifics, Options);

    /// <summary>
    /// A row that does not deserialise reads as <see cref="HoldingSpecifics.None"/> rather than
    /// throwing — the same call M12 made for a policy. A 500 on every screen that lists a client's
    /// products because one row's json was hand-edited is worse than a holding showing no terms,
    /// and "no terms" is the reading an agent can see is wrong.
    /// </summary>
    private static HoldingSpecifics Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HoldingSpecifics.None;

        try
        {
            return JsonSerializer.Deserialize<HoldingSpecifics>(json, Options)
                   ?? HoldingSpecifics.None;
        }
        catch (JsonException)
        {
            return HoldingSpecifics.None;
        }
    }
}
