namespace Sankore.Modules.Leads.Domain;

using System.Text.Json.Nodes;

/// <summary>
/// Upgrades older schema versions of <see cref="SourceSettings"/> to the latest.
/// Called transparently on load via the EF ValueConverter.
/// The upgraded settings are written back on the next SaveChangesAsync.
/// </summary>
public static class SourceSettingsUpgrader
{
    /// <summary>Current schema version for all settings types.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Upgrades a deserialized settings instance to the current schema version.
    /// Returns the same instance if already current, or a new instance with defaults applied.
    /// </summary>
    public static SourceSettings Upgrade(SourceSettings settings)
    {
        if (settings.SchemaVersion >= CurrentVersion)
            return settings;

        // v1 → v2 rewrites the field mapping, which happens at the JSON level in
        // MigrateJson — by the time we have a typed instance there is nothing left to do.
        return settings with { SchemaVersion = CurrentVersion };
    }

    /// <summary>
    /// Rewrites a settings JSON document from the v1 shape to the current one, in place.
    /// Must run before deserialization: v1 stored the mapping as
    /// <c>{"fieldMapping": {"$.phone": "phoneNumber|e164:CI"}}</c>, v2 stores
    /// <c>{"fieldMappings": [{"sourceField": "$.phone", "targetField": "phoneNumber", ...}]}</c>.
    /// Unrecognized input is returned untouched.
    /// </summary>
    public static string MigrateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            return json;
        }

        if (node is not JsonObject obj) return json;

        var legacyKey = obj
            .Select(p => p.Key)
            .FirstOrDefault(k => k.Equals("fieldMapping", StringComparison.OrdinalIgnoreCase));

        if (legacyKey is null) return json;

        var alreadyMigrated = obj.Any(p =>
            p.Key.Equals("fieldMappings", StringComparison.OrdinalIgnoreCase));

        // Drop the v1 key; only build rules from it when v2 rules aren't already present
        var legacy = obj[legacyKey] as JsonObject;
        obj.Remove(legacyKey);

        if (!alreadyMigrated && legacy is not null)
        {
            obj["fieldMappings"] = BuildRules(legacy);
            obj["schemaVersion"] = CurrentVersion;
        }

        return obj.ToJsonString();
    }

    /// <summary>Converts v1 "target|transform|transform" expressions into rule objects.</summary>
    private static JsonArray BuildRules(JsonObject legacy)
    {
        var rules = new JsonArray();

        foreach (var (sourceField, expression) in legacy)
        {
            string? expr;
            try
            {
                expr = expression?.GetValue<string>();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(expr)) continue;

            var parts = expr.Split('|');
            var target = parts[0].Trim();
            if (target.Length == 0) continue;

            var rule = new JsonObject
            {
                ["sourceField"] = sourceField,
                ["targetField"] = target,
                ["transformation"] = nameof(FieldTransformation.None)
            };

            // v1 allowed several transforms per entry; v2 carries one.
            // e164 wins over trim (it normalizes whitespace anyway); the v1 'map' and
            // 'concat' transforms were identity no-ops, so they map to None.
            var transforms = parts.Skip(1).Select(p => p.Trim()).ToList();

            var e164 = transforms.FirstOrDefault(t =>
                t.StartsWith("e164:", StringComparison.OrdinalIgnoreCase));

            if (e164 is not null)
            {
                rule["transformation"] = nameof(FieldTransformation.E164);
                rule["e164Country"] = e164[5..].Trim();
            }
            else if (transforms.Any(t => t.Equals("trim", StringComparison.OrdinalIgnoreCase)))
            {
                rule["transformation"] = nameof(FieldTransformation.Trim);
            }

            rules.Add(rule);
        }

        return rules;
    }
}
