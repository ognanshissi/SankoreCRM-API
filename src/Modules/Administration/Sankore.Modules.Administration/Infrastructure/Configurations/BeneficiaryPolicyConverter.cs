namespace Sankore.Modules.Administration.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Maps <see cref="BeneficiaryPolicy"/> to a typed <c>jsonb</c> column. Same shape as M13's
/// <c>SourceSettingsConverter</c>: a converter for the round-trip and a comparer so EF can detect
/// that the policy changed.
/// </summary>
internal sealed class BeneficiaryPolicyConverter()
    : ValueConverter<BeneficiaryPolicy, string>(
        policy => Serialize(policy),
        json => Deserialize(json))
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(BeneficiaryPolicy policy)
        => JsonSerializer.Serialize(policy, Options);

    /// <summary>
    /// A row that does not deserialise reads as <see cref="BeneficiaryPolicy.None"/> rather than
    /// throwing. The alternative is a 500 on every screen that lists the catalogue because one
    /// product's json was hand-edited — and <c>None</c> is the restrictive answer, so the failure
    /// mode is "this line collects nothing" rather than "this line allows anything".
    /// </summary>
    private static BeneficiaryPolicy Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BeneficiaryPolicy.None;

        try
        {
            return JsonSerializer.Deserialize<BeneficiaryPolicy>(json, Options)
                   ?? BeneficiaryPolicy.None;
        }
        catch (JsonException)
        {
            return BeneficiaryPolicy.None;
        }
    }
}

/// <summary>
/// Change detection for the policy.
///
/// <para>
/// EF's default comparer for a converted property compares the CONVERTED values, which here means
/// comparing two json strings — and <see cref="BeneficiaryPolicy"/> holds lists, whose property-wise
/// record equality does NOT compare elements. Without an explicit comparer, adding a role to
/// <c>AllowedRoles</c> would leave the entity looking unchanged and the write would be silently
/// dropped. Comparing the serialised form is the simplest thing that is actually correct.
/// </para>
/// </summary>
internal sealed class BeneficiaryPolicyComparer()
    : ValueComparer<BeneficiaryPolicy>(
        (left, right) => Equal(left, right),
        policy => JsonSerializer.Serialize(policy, BeneficiaryPolicyConverter.Options).GetHashCode(StringComparison.Ordinal),
        policy => Clone(policy))
{
    private static bool Equal(BeneficiaryPolicy? left, BeneficiaryPolicy? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return string.Equals(
            JsonSerializer.Serialize(left, BeneficiaryPolicyConverter.Options),
            JsonSerializer.Serialize(right, BeneficiaryPolicyConverter.Options),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A deep snapshot, so EF compares the policy as it was LOADED against the policy as it is
    /// now. A shallow copy would share the role list, and a mutation through it would be invisible.
    /// </summary>
    private static BeneficiaryPolicy Clone(BeneficiaryPolicy policy)
        => JsonSerializer.Deserialize<BeneficiaryPolicy>(
               JsonSerializer.Serialize(policy, BeneficiaryPolicyConverter.Options),
               BeneficiaryPolicyConverter.Options)
           ?? BeneficiaryPolicy.None;
}
