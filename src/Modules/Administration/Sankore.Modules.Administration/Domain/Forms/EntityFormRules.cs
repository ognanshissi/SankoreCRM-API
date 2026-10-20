namespace Sankore.Modules.Administration.Domain.Forms;

using System.Text.Json;
using System.Text.RegularExpressions;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;

/// <summary>
/// What a tenant may save as a form layout. Every refusal names the section or field it is about
/// (<c>CODE:key</c>), so the editor can point at it.
/// </summary>
internal static partial class EntityFormRules
{
    public const int MaxSections = 30;
    public const int MaxCustomFields = 150;
    public const int MaxOptions = 200;

    [GeneratedRegex("^[a-z][a-zA-Z0-9_-]{0,49}$")]
    private static partial Regex SectionKeyFormat();

    /// <summary>
    /// camelCase, letters and digits only. No dot, so a custom key can never collide with a nested
    /// native one (<c>address.city</c>), and no character a JSON path or a CSV header would need to
    /// escape.
    /// </summary>
    [GeneratedRegex("^[a-z][a-zA-Z0-9]{1,49}$")]
    private static partial Regex CustomKeyFormat();

    private static readonly FormFieldType[] ChoiceTypes = [FormFieldType.SingleChoice, FormFieldType.MultiChoice];
    private static readonly FormFieldType[] ConditionTargetTypes =
        [FormFieldType.SingleChoice, FormFieldType.MultiChoice, FormFieldType.Boolean];
    private static readonly FormFieldType[] NumericTypes =
        [FormFieldType.Integer, FormFieldType.Decimal, FormFieldType.Percent, FormFieldType.Money];
    private static readonly FormFieldType[] TextTypes =
        [FormFieldType.Text, FormFieldType.LongText, FormFieldType.Phone];

    /// <param name="previous">The layout currently stored, or null when the tenant never saved one.</param>
    public static Result<FormLayoutDocument> Normalise(
        FormEntity entity, FormLayoutDocument? previous, FormSectionsAndFields incoming)
    {
        var catalog = NativeFormCatalog.For(entity);

        // ── Sections ─────────────────────────────────────────────────────
        if (incoming.Sections.Count > MaxSections)
            return Fail("FORM_TOO_MANY_SECTIONS");

        var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in incoming.Sections)
        {
            if (section.Key is null || !SectionKeyFormat().IsMatch(section.Key))
                return Fail($"FORM_SECTION_KEY_INVALID:{section.Key}");
            if (!sectionKeys.Add(section.Key))
                return Fail($"FORM_SECTION_KEY_DUPLICATE:{section.Key}");
            if (string.IsNullOrWhiteSpace(section.Title) || section.Title.Trim().Length > 100)
                return Fail($"FORM_SECTION_TITLE_INVALID:{section.Key}");
        }

        // ── Fields ───────────────────────────────────────────────────────
        var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<FormField>(incoming.Fields.Count);

        foreach (var field in incoming.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
                return Fail("FORM_FIELD_KEY_INVALID:");
            if (!fieldKeys.Add(field.Key))
                return Fail($"FORM_FIELD_KEY_DUPLICATE:{field.Key}");
            if (field.SectionKey is not null && !sectionKeys.Contains(field.SectionKey))
                return Fail($"FORM_FIELD_UNKNOWN_SECTION:{field.Key}");
            if (string.IsNullOrWhiteSpace(field.Label) || field.Label.Trim().Length > 150)
                return Fail($"FORM_FIELD_LABEL_INVALID:{field.Key}");
            if (field.IsRequired && !field.QuickAdd)
                return Fail($"FORM_REQUIRED_FIELD_NOT_IN_QUICK_ADD:{field.Key}");
            if (field.IsRequired && (field.IsHidden || field.IsArchived))
                return Fail($"FORM_REQUIRED_FIELD_NOT_SHOWN:{field.Key}");

            var native = catalog.Find(field.Key);
            var result = native is not null ? Native(native, field) : Custom(field);
            if (result.IsFailure) return Fail(result.Error!);

            fields.Add(result.Value);
        }

        if (fields.Count(f => catalog.Find(f.Key) is null) > MaxCustomFields)
            return Fail("FORM_TOO_MANY_CUSTOM_FIELDS");

        // ── What was saved before ────────────────────────────────────────
        if (previous is not null)
        {
            foreach (var before in previous.Fields.Where(f => catalog.Find(f.Key) is null))
            {
                var now = fields.FirstOrDefault(f => f.Key == before.Key);

                // The layout service cannot see whether any record holds a value for it, so a
                // custom field once saved is archived, never removed — its values stay readable.
                if (now is null)
                    return Fail($"FORM_FIELD_CANNOT_BE_REMOVED:{before.Key}");

                // Stored values were checked against the old type; reinterpreting them under a new
                // one turns "2026-03-01" into a number nobody can read back.
                if (now.Type != before.Type)
                    return Fail($"FORM_FIELD_TYPE_IMMUTABLE:{before.Key}");
            }
        }

        // ── Conditions, against the final set (catalogue included) ───────
        var byKey = catalog.Fields
            .Where(n => !fieldKeys.Contains(n.Key))
            .Concat(fields.Select(f => catalog.Find(f.Key) ?? f))
            .ToDictionary(f => f.Key, StringComparer.Ordinal);

        foreach (var section in incoming.Sections)
        {
            if (!ConditionIsValid(section.VisibleWhen, self: null, byKey))
                return Fail($"FORM_CONDITION_INVALID:{section.Key}");
        }

        foreach (var field in fields)
        {
            if (field.IsNative) continue; // a native field keeps the catalogue's condition
            if (!ConditionIsValid(field.VisibleWhen, field.Key, byKey))
                return Fail($"FORM_CONDITION_INVALID:{field.Key}");
        }

        // ── Order: renumbered so the stored document says what the editor showed ──
        var orderedSections = incoming.Sections
            .Select((s, i) => (s, i))
            .OrderBy(x => x.s.Order).ThenBy(x => x.i)
            .Select((x, n) => x.s with
            {
                Title = x.s.Title.Trim(),
                Description = Trim(x.s.Description),
                Order = n + 1,
            })
            .ToList();

        var orderedFields = fields
            .Select((f, i) => (f, i))
            .GroupBy(x => x.f.SectionKey ?? string.Empty)
            .SelectMany(g => g
                .OrderBy(x => x.f.Order).ThenBy(x => x.i)
                .Select((x, n) => x.f with { Order = n + 1 }))
            .ToList();

        return Result.Ok(new FormLayoutDocument(orderedSections, orderedFields));
    }

    /// <summary>
    /// A native field keeps what belongs to the entity — type, options, limits, condition — and
    /// takes from the tenant only placement, wording, and the three flags.
    /// </summary>
    private static Result<FormField> Native(FormField native, FormField incoming)
    {
        if (incoming.IsArchived)
            return Result.Fail<FormField>($"FORM_NATIVE_FIELD_CANNOT_BE_ARCHIVED:{native.Key}");

        if (native.IsRequiredLocked && (!incoming.IsRequired || !incoming.QuickAdd || incoming.IsHidden))
            return Result.Fail<FormField>($"FORM_FIELD_LOCKED:{native.Key}");

        return Result.Ok(native with
        {
            Label = incoming.Label.Trim(),
            HelpText = Trim(incoming.HelpText),
            Placeholder = Trim(incoming.Placeholder),
            SectionKey = incoming.SectionKey,
            Order = incoming.Order,
            QuickAdd = incoming.QuickAdd,
            IsRequired = incoming.IsRequired,
            IsHidden = incoming.IsHidden,
        });
    }

    private static Result<FormField> Custom(FormField field)
    {
        var key = field.Key;
        Result<FormField> Refuse(string code) => Result.Fail<FormField>($"{code}:{key}");

        if (!CustomKeyFormat().IsMatch(key)) return Refuse("FORM_FIELD_KEY_INVALID");
        if (!Enum.IsDefined(field.Type)) return Refuse("FORM_FIELD_TYPE_NOT_SUPPORTED");
        if (field.Type == FormFieldType.Reference) return Refuse("FORM_FIELD_TYPE_NOT_SUPPORTED");
        if (field.IsHidden) return Refuse("FORM_FIELD_HIDE_ONLY_NATIVE");

        // Options
        IReadOnlyList<FormOption>? options = null;
        if (ChoiceTypes.Contains(field.Type))
        {
            if (field.Options is null || field.Options.Count == 0) return Refuse("FORM_FIELD_OPTIONS_REQUIRED");
            if (field.Options.Count > MaxOptions) return Refuse("FORM_FIELD_TOO_MANY_OPTIONS");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cleaned = new List<FormOption>(field.Options.Count);
            foreach (var option in field.Options)
            {
                var value = option.Value?.Trim();
                var label = option.Label?.Trim();
                if (string.IsNullOrEmpty(value) || value.Length > 100 || string.IsNullOrEmpty(label) || label.Length > 150)
                    return Refuse("FORM_FIELD_OPTION_INVALID");
                if (!seen.Add(value)) return Refuse("FORM_FIELD_OPTION_DUPLICATE");
                cleaned.Add(new FormOption(value, label));
            }
            options = cleaned;
        }

        // Limits only where they mean something; anything else would be stored and never applied.
        if ((field.Min is not null || field.Max is not null) && !NumericTypes.Contains(field.Type))
            return Refuse("FORM_FIELD_RANGE_NOT_APPLICABLE");
        if (field.Min is { } min && field.Max is { } max && min > max)
            return Refuse("FORM_FIELD_RANGE_INVALID");

        if (field.MaxLength is not null && !TextTypes.Contains(field.Type))
            return Refuse("FORM_FIELD_LENGTH_NOT_APPLICABLE");
        if (field.MaxLength is <= 0 or > 10_000)
            return Refuse("FORM_FIELD_LENGTH_INVALID");

        if (field.Pattern is not null)
        {
            if (!TextTypes.Contains(field.Type)) return Refuse("FORM_FIELD_PATTERN_NOT_APPLICABLE");
            try
            {
                _ = new Regex(field.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return Refuse("FORM_FIELD_PATTERN_INVALID");
            }
        }

        var normalised = field with
        {
            Label = field.Label.Trim(),
            HelpText = Trim(field.HelpText),
            Placeholder = Trim(field.Placeholder),
            Options = options,
            DefaultValue = Trim(field.DefaultValue),
            ReferenceTarget = null,
            IsNative = false,
            IsRequiredLocked = false,
        };

        // A default the field's own validation would refuse is a creation that fails for a value
        // nobody typed.
        if (normalised.DefaultValue is not null)
        {
            var probe = new EntityFormDto(FormEntity.Lead, 1, [], [normalised with { IsRequired = false, VisibleWhen = null }]);
            var check = CustomFieldValues.Apply(
                probe, null,
                new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(normalised.DefaultValue) },
                null, enforceRequired: false, isCreate: false);
            if (check.IsFailure || check.Value is null) return Refuse("FORM_FIELD_DEFAULT_INVALID");
        }

        return Result.Ok(normalised);
    }

    private static bool ConditionIsValid(
        FormCondition? condition, string? self, IReadOnlyDictionary<string, FormField> byKey)
    {
        if (condition is null) return true;
        if (condition.FieldKey == self) return false;
        if (!byKey.TryGetValue(condition.FieldKey ?? string.Empty, out var target)) return false;
        if (target.IsArchived || !ConditionTargetTypes.Contains(target.Type)) return false;
        if (condition.Values is null || condition.Values.Count == 0) return false;

        return target.Type == FormFieldType.Boolean
            ? condition.Values.All(v => v is "true" or "false")
            : condition.Values.All(v => target.Options?.Any(o => o.Value == v) ?? false);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result<FormLayoutDocument> Fail(string error) => Result.Fail<FormLayoutDocument>(error);
}
