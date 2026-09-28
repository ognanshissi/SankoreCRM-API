namespace Sankore.Shared.Infrastructure.Outbox;

using System.Text.Json;

/// <summary>
/// The serializer settings for outbox payloads, declared once so the write side and the read
/// side cannot disagree.
///
/// They used to: the publisher wrote camelCase while the processor deserialized with the
/// defaults, which are case-sensitive. Integration events are positional records, so
/// System.Text.Json builds them through their constructor and matches JSON properties to
/// constructor PARAMETERS — "tenantId" never matched "TenantId", every parameter fell back to
/// its default, and the bus published an event whose strings were null and whose Guids were
/// empty. Nothing failed at the publishing end; the damage only surfaced in a consumer, as a
/// NullReferenceException on the first string it touched.
///
/// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> is what actually closes the
/// hole, and it also lets rows written before this fix — or by any future change of naming
/// policy — still deserialize.
/// </summary>
public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
