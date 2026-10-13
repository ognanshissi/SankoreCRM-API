namespace Sankore.Modules.Administration.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Maps <see cref="KycRemotePolicy"/> to a typed <c>jsonb</c> column — the same pair as
/// <see cref="BeneficiaryPolicyConverter"/>, for the same two reasons: a converter for the
/// round-trip and a comparer so EF can see that the policy changed.
/// </summary>
internal sealed class KycRemotePolicyConverter()
    : ValueConverter<KycRemotePolicy, string>(
        policy => Serialize(policy),
        json => Deserialize(json))
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(KycRemotePolicy policy)
        => JsonSerializer.Serialize(policy, Options);

    /// <summary>
    /// A row that does not deserialise reads as <see cref="KycRemotePolicy.Disabled"/> rather than
    /// throwing — and here the fail-closed direction matters more than it does for the beneficiary
    /// policy: a corrupted row means no link is ever sent, never a link sent under rules nobody
    /// can read.
    /// </summary>
    private static KycRemotePolicy Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return KycRemotePolicy.Disabled;

        try
        {
            return JsonSerializer.Deserialize<KycRemotePolicy>(json, Options)
                   ?? KycRemotePolicy.Disabled;
        }
        catch (JsonException)
        {
            return KycRemotePolicy.Disabled;
        }
    }
}

/// <summary>
/// Change detection for the remote-KYC policy.
///
/// <para>
/// Needed for exactly the reason <see cref="BeneficiaryPolicyComparer"/> is: the record holds
/// LISTS (<c>RequiredDocuments</c>, <c>AllowedDocumentTypes</c>), and record equality compares
/// members property-wise without comparing list elements. Without this, adding a required document
/// would leave the entity looking unchanged and the write would be silently dropped.
/// </para>
/// </summary>
internal sealed class KycRemotePolicyComparer()
    : ValueComparer<KycRemotePolicy>(
        (left, right) => Equal(left, right),
        policy => JsonSerializer.Serialize(policy, KycRemotePolicyConverter.Options)
            .GetHashCode(StringComparison.Ordinal),
        policy => Clone(policy))
{
    private static bool Equal(KycRemotePolicy? left, KycRemotePolicy? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return string.Equals(
            JsonSerializer.Serialize(left, KycRemotePolicyConverter.Options),
            JsonSerializer.Serialize(right, KycRemotePolicyConverter.Options),
            StringComparison.Ordinal);
    }

    /// <summary>Deep, so a mutation through a shared list cannot stay invisible.</summary>
    private static KycRemotePolicy Clone(KycRemotePolicy policy)
        => JsonSerializer.Deserialize<KycRemotePolicy>(
               JsonSerializer.Serialize(policy, KycRemotePolicyConverter.Options),
               KycRemotePolicyConverter.Options)
           ?? KycRemotePolicy.Disabled;
}
