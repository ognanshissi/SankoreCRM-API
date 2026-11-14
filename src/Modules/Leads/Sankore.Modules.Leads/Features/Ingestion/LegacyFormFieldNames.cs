namespace Sankore.Modules.Leads.Features.Ingestion;

/// <summary>
/// The hosted-form catalogue used to name three fields with names the ingestion never read:
/// <c>productCode</c>, <c>message</c> and <c>city</c> (2026-11-14, docs/formulaire-heberge.md lot 1).
/// The catalogue now uses the CRM's names, but forms saved before keep posting the old ones — and
/// a page may hold the old definition for five minutes — so a payload without mapping rules still
/// carries them onto the lead: the product to <c>interestedProduct</c>, the message to
/// <c>comment</c>, the city appended to the comment (a lead captured from the web has no city).
/// A value under the CRM's own name always wins.
/// </summary>
internal static class LegacyFormFieldNames
{
    public static void Apply(IDictionary<string, string?> fields)
    {
        if (Value(fields, "interestedProduct") is null && Value(fields, "productCode") is { } product)
            fields["interestedProduct"] = product;

        var comment = Value(fields, "comment") ?? Value(fields, "message");
        if (Value(fields, "city") is { } city)
            comment = comment is null ? $"Ville : {city}" : $"{comment}\nVille : {city}";
        if (comment is not null)
            fields["comment"] = comment;
    }

    private static string? Value(IDictionary<string, string?> fields, string key)
        => fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
