namespace Sankore.Modules.Integration.Features.Commands;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Turns a command's payload into the two columns the aggregate holds (INT-05, criterion 3):
/// the ciphertext, and the list of FIELD NAMES in clear.
///
/// <para>
/// The split is the whole point. A command payload is a customer's identity document, address
/// and declared income on its way out of the platform, and it sits at rest for as long as the
/// external system is down — so the value is AES-256-GCM under this module's own key. But a
/// rejection queue nobody can read is a rejection queue nobody works: "which fields did we send"
/// has to be answerable without "what were their values" being answerable, and that is exactly
/// what <c>payload_field_names</c> buys.
/// </para>
///
/// <para>
/// This does NOT re-implement <see cref="Sankore.Shared.Infrastructure.Behaviors.SanitizedJsonSerializer"/>.
/// That type already does the "keep the name, redact the value" job for the AUDIT row, driven by
/// <c>[SensitiveData]</c>, and the audit pipeline calls it for every MediatR command. The job
/// here is the complementary one: making sure the payload reaches the database encrypted and the
/// audit through that serializer — never the other way round. Any command record of this module
/// that carries a payload marks it <c>[property: SensitiveData]</c> and exposes its field names
/// as an ordinary, non-sensitive property, so the audit row shows
/// <c>{"payloadFieldNames":["phoneNumber",…],"correctedPayloadJson":"***"}</c>.
/// </para>
/// </summary>
internal sealed class CommandPayloadProtector(
    [FromKeyedServices(IntegrationFieldProtection.Key)] IFieldEncryptor encryptor)
{
    /// <summary>
    /// Wire options for a stored payload. camelCase and enums-as-names, like every other
    /// serialised shape in this repo: the field names computed below are shown to an operator and
    /// end up in an audit row, and <c>KycLevel: 2</c> would be unreadable there — and would also
    /// change meaning the day somebody renumbers the enum.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serialises, encrypts, and lists the TOP-LEVEL property names of the serialised object.
    ///
    /// <para>
    /// Top-level only, and deliberately so: a nested beneficiary list would otherwise flatten
    /// into one name per beneficiary per field, which reveals how many beneficiaries there are —
    /// a detail the field-name list is not supposed to leak. The name of the collection is
    /// enough to answer "did we send beneficiaries".
    /// </para>
    /// </summary>
    internal ProtectedPayload Protect<TPayload>(TPayload payload) where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, payload.GetType(), Options);
        return new ProtectedPayload(encryptor.Encrypt(json)!, FieldNamesOf(json));
    }

    /// <summary>
    /// Same, from JSON an operator supplied — the corrected payload of a replay. Returns
    /// <c>null</c> when the text is not a JSON OBJECT: a caller that sends an array or a bare
    /// string has sent something whose field names cannot be computed, and storing it would leave
    /// a command whose audit trail describes a payload it does not have.
    /// </summary>
    internal ProtectedPayload? ProtectJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject obj) return null;

        // Re-serialised from the parsed node rather than stored as received: it normalises
        // whitespace and guarantees what we encrypt is exactly what the field names describe.
        var canonical = obj.ToJsonString();
        return new ProtectedPayload(encryptor.Encrypt(canonical)!, [.. obj.Select(p => p.Key).Order()]);
    }

    /// <summary>
    /// Reverses <see cref="Protect{TPayload}"/>. Returns <c>null</c> for an absent payload and
    /// for one that no longer deserialises into <typeparamref name="TPayload"/> — the dispatcher
    /// turns that into a <c>Technical</c> rejection naming the command, which is far better than
    /// an exception that would retry the same unreadable bytes eight times.
    /// </summary>
    internal TPayload? Unprotect<TPayload>(string? ciphertext) where TPayload : class
    {
        if (string.IsNullOrWhiteSpace(ciphertext)) return null;

        try
        {
            var json = encryptor.Decrypt(ciphertext);
            return json is null ? null : JsonSerializer.Deserialize<TPayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The top-level property names of a serialised object, sorted. Sorted because the aggregate
    /// stores them joined and an operator comparing two commands should not be told they differ
    /// because a serialiser emitted the same fields in another order.
    /// </summary>
    private static IReadOnlyList<string> FieldNamesOf(string json)
    {
        var node = JsonNode.Parse(json);
        return node is JsonObject obj ? [.. obj.Select(p => p.Key).Order()] : [];
    }
}

/// <summary>
/// What goes into the two columns: the AES-256-GCM payload, and the names of the fields it
/// carries. Never a value.
/// </summary>
internal sealed record ProtectedPayload(string Ciphertext, IReadOnlyList<string> FieldNames);
