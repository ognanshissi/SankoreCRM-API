namespace Sankore.Modules.Administration.PublicApi.Forms;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sankore.Shared.Kernel;

/// <summary>
/// Checks and merges the custom-field values of one lead, client or product against its tenant's
/// form. The single definition of "is this value acceptable", shared by every module that stores
/// custom values — a pure function over the form and the payload, so each rule has a test of its
/// own.
///
/// <para>
/// <b>Merge, not replace.</b> A key present with a value overwrites, a key present with
/// <c>null</c> clears, a key absent is left alone. The fiche posts the sections an agent edited,
/// and a replace would make saving one section erase the others.
/// </para>
///
/// <para>
/// <b>Required is enforced only when the caller says so.</b> An import, a web ingest or a scheduled
/// pull has never heard of a field an administrator added this morning, and refusing its rows would
/// turn a form setting into lost prospects. Those paths still have their values TYPE-checked; only
/// the interactive creation and the fiche enforce presence.
/// </para>
/// </summary>
public static class CustomFieldValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <param name="currentJson">The stored object, or null for a new record.</param>
    /// <param name="changes">What the caller sent; null when it sent nothing.</param>
    /// <param name="nativeValues">
    /// The record's native values that a condition may name (<c>prospectType</c>, <c>clientType</c>,
    /// <c>category</c>…), as their wire strings.
    /// </param>
    /// <param name="enforceRequired">
    /// True for the interactive paths. On a creation every visible required field must then hold a
    /// value; on an update only a field being CLEARED is checked, so a field made required after
    /// the record was created does not block an unrelated edit.
    /// </param>
    /// <returns>The JSON object to store, or null when it holds no value at all.</returns>
    public static Result<string?> Apply(
        EntityFormDto form,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?>? nativeValues,
        bool enforceRequired,
        bool isCreate)
    {
        var fields = form.Fields
            .Where(f => !f.IsNative)
            .ToDictionary(f => f.Key, StringComparer.Ordinal);

        var merged = Parse(currentJson);

        if (changes is not null)
        {
            foreach (var (given, raw) in changes)
            {
                // Exact first; a header or a mapping typed with another casing (« custom.Segment »)
                // still lands on its field when it names only one.
                var key = fields.ContainsKey(given)
                    ? given
                    : fields.Keys.Where(k => string.Equals(k, given, StringComparison.OrdinalIgnoreCase))
                        .SingleOrDefault() ?? given;

                if (!fields.TryGetValue(key, out var field))
                    return Result.Fail<string?>($"CUSTOM_FIELD_UNKNOWN:{key}");

                if (field.IsArchived)
                    return Result.Fail<string?>($"CUSTOM_FIELD_ARCHIVED:{key}");

                if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || IsBlankString(raw))
                {
                    merged.Remove(key);
                    continue;
                }

                var normalised = Normalise(field, raw, form.Currency, StoredCurrency(merged, key));
                if (normalised.IsFailure)
                    return Result.Fail<string?>(normalised.Error!);

                // An empty multi-choice normalises to nothing, which is a clear.
                if (normalised.Value is null) merged.Remove(key);
                else merged[key] = normalised.Value;
            }
        }

        if (isCreate)
        {
            // A default fills a field the caller did not send; one sent as null was cleared on
            // purpose and stays empty.
            foreach (var field in fields.Values)
            {
                if (field.IsArchived || field.DefaultValue is null) continue;
                if (merged.ContainsKey(field.Key) || (changes?.ContainsKey(field.Key) ?? false)) continue;

                var normalised = Normalise(field, JsonSerializer.SerializeToElement(field.DefaultValue, Json), form.Currency, stored: null);
                if (normalised.IsSuccess)
                    merged[field.Key] = normalised.Value;
            }
        }

        if (enforceRequired)
        {
            var sections = form.Sections.ToDictionary(s => s.Key, StringComparer.Ordinal);

            foreach (var field in fields.Values)
            {
                if (!field.IsRequired || field.IsArchived || merged.ContainsKey(field.Key)) continue;

                // On an update, only what the caller is clearing is its responsibility.
                if (!isCreate && !(changes?.ContainsKey(field.Key) ?? false)) continue;

                if (!IsVisible(field, sections, merged, nativeValues)) continue;

                return Result.Fail<string?>($"CUSTOM_FIELD_REQUIRED:{field.Key}");
            }
        }

        return Result.Ok<string?>(merged.Count == 0 ? null : merged.ToJsonString(Json));
    }

    /// <summary>
    /// <see cref="Apply"/> one value at a time, keeping those the form accepts and setting aside the
    /// others — for the paths where the sender cannot be asked to correct anything: a web form, a
    /// webhook, a scheduled pull, a lead converted into a client. Losing a prospect to a malformed
    /// optional value is the one outcome those paths must not have; the rejected keys are returned so
    /// the caller can say what was dropped.
    /// </summary>
    public static (string? Json, IReadOnlyList<string> Rejected) ApplyBestEffort(
        EntityFormDto form,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?>? nativeValues)
    {
        var json = currentJson;
        var rejected = new List<string>();
        foreach (var (key, value) in changes ?? new Dictionary<string, JsonElement>())
        {
            var one = Apply(form, json, new Dictionary<string, JsonElement> { [key] = value },
                nativeValues, enforceRequired: false, isCreate: false);
            if (one.IsSuccess) json = one.Value;
            else rejected.Add(key);
        }
        return (json, rejected);
    }

    /// <summary>
    /// List filters on custom values — <c>key:value</c>, as a list screen sends them — as the JSON
    /// documents a jsonb containment (<c>custom_fields @&gt; doc</c>) needs, one per filter.
    ///
    /// <para>
    /// The value goes through the same normalisation as a write, so « Commerçant » finds the
    /// stored option value <c>Commercant</c>, « oui » finds <c>true</c>, and a multi-choice filter
    /// becomes <c>["value"]</c> — the only shape jsonb containment matches inside an array.
    /// </para>
    ///
    /// <para>
    /// <b>A field marked sensitive cannot be filtered on</b> (<c>CUSTOM_FILTER_SENSITIVE</c>):
    /// narrowing a list by a value the screens mask is a way of reading it back one guess at a time.
    /// An unknown key is refused rather than ignored — an ignored filter shows the whole list under
    /// a heading that says it is filtered.
    /// </para>
    /// </summary>
    public static Result<IReadOnlyList<string>> FilterDocuments(EntityFormDto form, IEnumerable<string>? filters)
    {
        var documents = new List<string>();
        foreach (var filter in filters ?? [])
        {
            var separator = filter.IndexOf(':');
            if (separator <= 0) return Result.Fail<IReadOnlyList<string>>($"CUSTOM_FILTER_INVALID:{filter}");

            var given = filter[..separator].Trim();
            var text = filter[(separator + 1)..];
            var field = form.Fields.FirstOrDefault(f => !f.IsNative && f.Key == given)
                        ?? form.Fields.Where(f => !f.IsNative && string.Equals(f.Key, given, StringComparison.OrdinalIgnoreCase))
                            .SingleOrDefault();

            if (field is null) return Result.Fail<IReadOnlyList<string>>($"CUSTOM_FILTER_UNKNOWN:{given}");
            if (field.IsSensitive) return Result.Fail<IReadOnlyList<string>>($"CUSTOM_FILTER_SENSITIVE:{field.Key}");

            // The field alone, unconditional and optional: a filter asks "which records hold this
            // value", not "would this record be valid".
            var probe = new EntityFormDto(form.Entity, form.Version, [],
                [field with { IsArchived = false, IsRequired = false, VisibleWhen = null, Min = null, Max = null, Pattern = null, MaxLength = null }]);
            var normalised = Apply(probe, null,
                new Dictionary<string, JsonElement> { [field.Key] = JsonSerializer.SerializeToElement(text) },
                null, enforceRequired: false, isCreate: false);

            if (normalised.IsFailure || normalised.Value is null)
                return Result.Fail<IReadOnlyList<string>>($"CUSTOM_FILTER_INVALID:{field.Key}");

            documents.Add(normalised.Value);
        }
        return Result.Ok<IReadOnlyList<string>>(documents);
    }

    /// <summary>
    /// A refusal of <see cref="Apply"/> as the line of an import report: the column to correct and
    /// why. Null when the error is not about a custom value, so the caller keeps its own message.
    /// </summary>
    public static string? DescribeForImport(string? error)
    {
        if (error is null || !error.StartsWith("CUSTOM_FIELD", StringComparison.Ordinal)) return null;
        var parts = error.Split(':', 2);
        var column = parts.Length == 2 ? $"Colonne {TextPrefix}{parts[1]}" : "Champs personnalisés";
        var why = parts[0] switch
        {
            "CUSTOM_FIELD_UNKNOWN" => "aucun champ personnalisé ne porte cet identifiant",
            "CUSTOM_FIELD_ARCHIVED" => "ce champ est archivé",
            "CUSTOM_FIELD_INVALID" => "valeur illisible pour ce type de champ",
            "CUSTOM_FIELD_UNKNOWN_OPTION" => "valeur absente des options du champ",
            "CUSTOM_FIELD_OUT_OF_RANGE" => "valeur hors des limites du champ",
            "CUSTOM_FIELD_TOO_LONG" => "texte trop long",
            "CUSTOM_FIELD_PATTERN_MISMATCH" => "format non respecté",
            "CUSTOM_FIELD_CURRENCY_NOT_INSTITUTION" => "montant dans une autre monnaie que celle de l'institution",
            "CUSTOM_FIELDS_UNAVAILABLE" => "réglages du formulaire indisponibles, relancez l'import",
            _ => "valeur refusée",
        };
        return $"{column} : {why}.";
    }

    /// <summary>
    /// The values a text source carries under <c>custom.&lt;key&gt;</c> — a spreadsheet column, a
    /// mapping target — as the dictionary <see cref="Apply"/> takes. Blank cells are left out: in a
    /// file they mean "no value", not "clear it".
    /// </summary>
    public const string TextPrefix = "custom.";

    public static Dictionary<string, JsonElement> FromText(IEnumerable<KeyValuePair<string, string?>> source)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, text) in source)
        {
            if (!name.StartsWith(TextPrefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(text)) continue;
            var key = name[TextPrefix.Length..].Trim();
            if (key.Length > 0) values[key] = JsonSerializer.SerializeToElement(text.Trim());
        }
        return values;
    }

    /// <summary>
    /// The stored object as the dictionary a DTO returns — null when nothing is stored or the
    /// column holds something that is not an object (never written by <see cref="Apply"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The native fields a tenant made required that the record leaves empty, in form order. Each
    /// module asks it at creation with the values it received, so "required" set in the form editor
    /// is enforced on the server and not only on the screen.
    ///
    /// <para>
    /// The fields the ENTITY requires (<see cref="FormField.IsRequiredLocked"/>) are left to the
    /// module's own validator, which knows their alternatives — a lead named by <c>fullName</c>
    /// alone has no <c>firstName</c> and is still valid.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredNatives(
        EntityFormDto form, IReadOnlyDictionary<string, string?> nativeValues)
    {
        var sections = form.Sections.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var none = new JsonObject();

        return form.Fields
            .Where(f => f.IsNative && f.IsRequired && !f.IsRequiredLocked && !f.IsHidden)
            .Where(f => nativeValues.TryGetValue(f.Key, out var v) ? string.IsNullOrWhiteSpace(v) : true)
            .Where(f => IsVisible(f, sections, none, nativeValues))
            .Select(f => f.Key)
            .ToList();
    }

    /// <summary>
    /// Whether the field is shown for this record: its own condition and its section's both hold.
    /// A condition naming a field the record has no value for is NOT satisfied — a company-only
    /// field stays hidden until the type says company.
    /// </summary>
    public static bool IsVisible(
        FormField field,
        IReadOnlyDictionary<string, FormSection> sections,
        JsonObject customValues,
        IReadOnlyDictionary<string, string?>? nativeValues)
    {
        if (!Holds(field.VisibleWhen, customValues, nativeValues)) return false;

        return field.SectionKey is null
            || !sections.TryGetValue(field.SectionKey, out var section)
            || Holds(section.VisibleWhen, customValues, nativeValues);
    }

    private static bool Holds(
        FormCondition? condition, JsonObject customValues, IReadOnlyDictionary<string, string?>? nativeValues)
    {
        if (condition is null) return true;

        IEnumerable<string> actual;

        if (customValues.TryGetPropertyValue(condition.FieldKey, out var node) && node is not null)
        {
            actual = node is JsonArray array
                ? array.Select(n => n?.ToString() ?? string.Empty)
                : [ConditionText(node)];
        }
        else if (nativeValues is not null
                 && nativeValues.TryGetValue(condition.FieldKey, out var native)
                 && native is not null)
        {
            actual = [native];
        }
        else
        {
            return false;
        }

        return actual.Any(a => condition.Values.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    private static string ConditionText(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => node.ToString(),
    };

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool IsBlankString(JsonElement raw)
        => raw.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(raw.GetString());

    /// <summary>The currency a Money value already stored under <paramref name="key"/> has, if any.</summary>
    private static string? StoredCurrency(JsonObject merged, string key) =>
        merged[key] is JsonObject money && money["currency"] is JsonValue code && code.TryGetValue<string>(out var c) ? c : null;

    /// <param name="institution">
    /// The form's currency (<see cref="EntityFormDto.Currency"/>): a Money value takes it when sent
    /// bare and must be in it — or in <paramref name="stored"/>, its own before a change of currency.
    /// Null: no constraint, a bare amount takes <see cref="InstitutionCurrencies.Default"/>.
    /// </param>
    private static Result<JsonNode?> Normalise(FormField field, JsonElement raw, string? institution = null, string? stored = null)
    {
        var key = field.Key;
        Result<JsonNode?> Invalid() => Result.Fail<JsonNode?>($"CUSTOM_FIELD_INVALID:{key}");

        switch (field.Type)
        {
            case FormFieldType.Text:
            case FormFieldType.LongText:
            case FormFieldType.Phone:
            {
                if (raw.ValueKind != JsonValueKind.String) return Invalid();
                var text = raw.GetString()!.Trim();
                if (field.MaxLength is { } max && text.Length > max)
                    return Result.Fail<JsonNode?>($"CUSTOM_FIELD_TOO_LONG:{key}");
                if (field.Pattern is not null && !Matches(field.Pattern, text))
                    return Result.Fail<JsonNode?>($"CUSTOM_FIELD_PATTERN_MISMATCH:{key}");
                return Result.Ok<JsonNode?>(JsonValue.Create(text));
            }

            case FormFieldType.Email:
            {
                if (raw.ValueKind != JsonValueKind.String) return Invalid();
                var text = raw.GetString()!.Trim();
                var at = text.IndexOf('@');
                if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1 || text.Contains(' '))
                    return Invalid();
                return Result.Ok<JsonNode?>(JsonValue.Create(text));
            }

            case FormFieldType.Url:
            {
                if (raw.ValueKind != JsonValueKind.String) return Invalid();
                var text = raw.GetString()!.Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return Invalid();
                return Result.Ok<JsonNode?>(JsonValue.Create(text));
            }

            case FormFieldType.Integer:
            case FormFieldType.Decimal:
            case FormFieldType.Percent:
            {
                if (!TryReadNumber(raw, out var number)) return Invalid();
                if (field.Type == FormFieldType.Integer && number != decimal.Truncate(number)) return Invalid();
                if (field.Type == FormFieldType.Percent && (number < 0 || number > 100))
                    return Result.Fail<JsonNode?>($"CUSTOM_FIELD_OUT_OF_RANGE:{key}");
                if (OutOfRange(field, number)) return Result.Fail<JsonNode?>($"CUSTOM_FIELD_OUT_OF_RANGE:{key}");
                return Result.Ok<JsonNode?>(JsonValue.Create(number));
            }

            case FormFieldType.Money:
            {
                // Text — a spreadsheet cell, a web form — reads « 1 500 000 XOF », « XOF 1500000 »
                // or a bare amount, which takes the platform's currency.
                if (raw.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    if (!TryReadMoneyText(raw.ValueKind == JsonValueKind.Number ? raw.GetRawText() : raw.GetString()!,
                            out var textAmount, out var textCurrency, institution))
                        return Invalid();
                    if (OutOfRange(field, textAmount)) return Result.Fail<JsonNode?>($"CUSTOM_FIELD_OUT_OF_RANGE:{key}");
                    if (!InInstitutionCurrency(textCurrency, institution, stored))
                        return Result.Fail<JsonNode?>($"CUSTOM_FIELD_CURRENCY_NOT_INSTITUTION:{key}");
                    return Result.Ok<JsonNode?>(new JsonObject { ["amount"] = textAmount, ["currency"] = textCurrency });
                }

                if (raw.ValueKind != JsonValueKind.Object
                    || !raw.TryGetProperty("amount", out var amountRaw)
                    || !TryReadNumber(amountRaw, out var amount)
                    || !raw.TryGetProperty("currency", out var currencyRaw)
                    || currencyRaw.ValueKind != JsonValueKind.String)
                    return Invalid();

                var currency = currencyRaw.GetString()!.Trim().ToUpperInvariant();
                if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper)) return Invalid();
                if (OutOfRange(field, amount)) return Result.Fail<JsonNode?>($"CUSTOM_FIELD_OUT_OF_RANGE:{key}");
                if (!InInstitutionCurrency(currency, institution, stored))
                    return Result.Fail<JsonNode?>($"CUSTOM_FIELD_CURRENCY_NOT_INSTITUTION:{key}");

                return Result.Ok<JsonNode?>(new JsonObject { ["amount"] = amount, ["currency"] = currency });
            }

            case FormFieldType.Date:
            {
                // ISO first; a French day-first date is what a spreadsheet or a person types.
                if (raw.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(raw.GetString()!.Trim(), ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"],
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return Invalid();
                return Result.Ok<JsonNode?>(JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            case FormFieldType.DateTime:
            {
                if (raw.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(raw.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var instant))
                    return Invalid();
                // UTC, like every timestamp column of this solution.
                return Result.Ok<JsonNode?>(JsonValue.Create(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            }

            case FormFieldType.Boolean:
            {
                if (raw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return Result.Ok<JsonNode?>(JsonValue.Create(raw.GetBoolean()));
                if (raw.ValueKind == JsonValueKind.String && TryReadBoolean(raw.GetString()!, out var parsed))
                    return Result.Ok<JsonNode?>(JsonValue.Create(parsed));
                if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var bit) && bit is 0 or 1)
                    return Result.Ok<JsonNode?>(JsonValue.Create(bit == 1));
                return Invalid();
            }

            case FormFieldType.SingleChoice:
            {
                if (raw.ValueKind != JsonValueKind.String) return Invalid();
                var value = OptionValueOf(field, raw.GetString()!);
                if (value is null) return Result.Fail<JsonNode?>($"CUSTOM_FIELD_UNKNOWN_OPTION:{key}");
                return Result.Ok<JsonNode?>(JsonValue.Create(value));
            }

            case FormFieldType.MultiChoice:
            {
                // A text list — « Commerce; Agriculture » — is what one spreadsheet cell holds.
                IEnumerable<JsonElement> items;
                if (raw.ValueKind == JsonValueKind.Array) items = raw.EnumerateArray();
                else if (raw.ValueKind == JsonValueKind.String)
                    items = raw.GetString()!
                        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(part => JsonSerializer.SerializeToElement(part));
                else return Invalid();

                var values = new List<string>();
                foreach (var item in items)
                {
                    if (item.ValueKind != JsonValueKind.String) return Invalid();
                    var value = OptionValueOf(field, item.GetString()!);
                    if (value is null) return Result.Fail<JsonNode?>($"CUSTOM_FIELD_UNKNOWN_OPTION:{key}");
                    if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
                }
                if (values.Count == 0) return Result.Ok<JsonNode?>(null);
                return Result.Ok<JsonNode?>(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
            }

            default:
                // Reference is native only in lot 1 — the form service refuses a custom one.
                return Invalid();
        }
    }

    private static bool TryReadNumber(JsonElement raw, out decimal number)
    {
        if (raw.ValueKind == JsonValueKind.Number) return raw.TryGetDecimal(out number);
        if (raw.ValueKind == JsonValueKind.String)
            return TryReadDecimalText(raw.GetString()!, out number);
        number = 0;
        return false;
    }

    private static bool OutOfRange(FormField field, decimal number)
        => (field.Min is { } min && number < min) || (field.Max is { } max && number > max);

    /// <summary>
    /// The stored value of an option, given its value or — from text typed by a person — its
    /// label, casing and surrounding spaces forgiven. Null when it is neither.
    /// </summary>
    private static string? OptionValueOf(FormField field, string given)
    {
        var options = field.Options ?? [];
        var exact = options.FirstOrDefault(o => string.Equals(o.Value, given, StringComparison.Ordinal));
        if (exact is not null) return exact.Value;

        var trimmed = given.Trim();
        return options.FirstOrDefault(o =>
                string.Equals(o.Value, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Label, trimmed, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static readonly string[] TrueWords = ["true", "oui", "vrai", "yes", "1", "x"];
    private static readonly string[] FalseWords = ["false", "non", "faux", "no", "0"];

    private static bool TryReadBoolean(string text, out bool value)
    {
        var t = text.Trim();
        if (TrueWords.Contains(t, StringComparer.OrdinalIgnoreCase)) { value = true; return true; }
        if (FalseWords.Contains(t, StringComparer.OrdinalIgnoreCase)) { value = false; return true; }
        value = false;
        return false;
    }

    /// <summary>The currency a bare amount takes when the form carries none — the zone this platform first served.</summary>
    public const string DefaultCurrency = InstitutionCurrencies.Default;

    /// <summary>No institution currency (a form without one): any currency, as before.</summary>
    private static bool InInstitutionCurrency(string currency, string? institution, string? stored) =>
        institution is null || InstitutionCurrencies.Resolve(currency, institution, stored).IsSuccess;

    private static bool TryReadMoneyText(string text, out decimal amount, out string currency, string? institution = null)
    {
        currency = string.IsNullOrWhiteSpace(institution) ? DefaultCurrency : institution.Trim().ToUpperInvariant();
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var code = parts.FirstOrDefault(p => p.Length == 3 && p.All(char.IsAsciiLetter));
        if (code is not null)
        {
            currency = code.ToUpperInvariant();
            parts.Remove(code);
        }
        return TryReadDecimalText(string.Concat(parts), out amount);
    }

    /// <summary>
    /// A number typed in French as well as in the invariant form: « 1 500 000,50 », « 1500000.5 ».
    /// Spaces (including the non-breaking ones a spreadsheet inserts) are thousands separators.
    /// </summary>
    private static bool TryReadDecimalText(string text, out decimal number)
    {
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '\u00A0' && c != '\u202F').ToArray());
        if (compact.Contains(',') && !compact.Contains('.')) compact = compact.Replace(',', '.');
        return decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static bool Matches(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
