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
    public const int CurrentVersion = 3;

    /// <summary>
    /// Upgrades a deserialized settings instance to the current schema version.
    /// Returns the same instance if already current, or a new instance with defaults applied.
    /// </summary>
    public static SourceSettings Upgrade(SourceSettings settings)
    {
        if (settings.SchemaVersion >= CurrentVersion)
            return settings;

        // Both migrations happen at the JSON level in MigrateJson — v1 → v2 rewrote the field
        // mapping, v2 → v3 moved the flat properties into the editor's blocks. By the time we
        // have a typed instance there is nothing left to do.
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

        var changed = MigrateFieldMapping(obj);
        changed |= MigrateFlatPropertiesIntoBlocks(obj);

        if (!changed) return json;

        obj["schemaVersion"] = CurrentVersion;
        return obj.ToJsonString();
    }

    /// <summary>v1 → v2: the mapping dictionary becomes a list of rule objects.</summary>
    private static bool MigrateFieldMapping(JsonObject obj)
    {
        var legacyKey = Key(obj, "fieldMapping");
        if (legacyKey is null) return false;

        var alreadyMigrated = Key(obj, "fieldMappings") is not null;

        // Drop the v1 key; only build rules from it when v2 rules aren't already present
        var legacy = obj[legacyKey] as JsonObject;
        obj.Remove(legacyKey);

        if (alreadyMigrated || legacy is null) return true;

        obj["fieldMappings"] = BuildRules(legacy);
        return true;
    }

    /// <summary>
    /// v2 → v3: the flat per-mode properties move into the blocks the settings editor writes
    /// (<c>script</c>, <c>hostedForm</c>, <c>pull</c>) and <c>allowedIpAddresses</c> takes the
    /// editor's name.
    ///
    /// <para>
    /// Not optional, and not cosmetic: v3 reads those values THROUGH the blocks, so a row left
    /// in the v2 shape would load with an empty provider API, no allowed origins and no IP
    /// allow-list — every already-configured source silently reset. Driven by key presence
    /// rather than by <c>$mode</c>, because this also runs on an inbound HTTP body, where the
    /// discriminator may be absent and is inferred later.
    /// </para>
    ///
    /// <para>
    /// Each block is skipped when the editor's key is already there: a v3 payload must never be
    /// rewritten from leftovers, and a mixed row keeps what the editor last wrote.
    /// </para>
    /// </summary>
    private static bool MigrateFlatPropertiesIntoBlocks(JsonObject obj)
    {
        var changed = MigrateWebhookAllowList(obj);
        changed |= MigratePull(obj);
        changed |= MigrateScript(obj);
        return changed;
    }

    private static bool MigrateWebhookAllowList(JsonObject obj)
    {
        var legacy = Key(obj, "allowedIpAddresses");
        if (legacy is null) return false;

        var value = obj[legacy];
        obj.Remove(legacy);

        if (Key(obj, "allowedIps") is null && value is not null)
            obj["allowedIps"] = value.DeepClone();

        return true;
    }

    private static bool MigratePull(JsonObject obj)
    {
        // The v2 keys that only a ScheduledPull row carried. `externalIdPath` is deliberately
        // NOT one of them: a ServerWebhook row carries it too and keeps it flat in v3, so it is
        // only moved once these have proved the row is a pull.
        string[] markers = ["endpointUrl", "httpMethod", "cronSchedule", "itemsPath", "cursorPath", "pagination"];
        if (!markers.Any(m => Key(obj, m) is not null)) return false;

        var hasBlock = Key(obj, "pull") is not null;
        var pull = new JsonObject();

        Move(obj, "endpointUrl", pull, "baseUrl");
        Move(obj, "httpMethod", pull, "requestMethod");
        Move(obj, "cronSchedule", pull, "cronExpression");
        Move(obj, "itemsPath", pull, "dataJsonPath");
        Move(obj, "externalIdPath", pull, "idJsonPath");
        Move(obj, "cursorPath", pull, "cursorJsonPath");
        Move(obj, "pagination", pull, "paginationStrategy");
        Move(obj, "basicAuthUsername", pull, "basicUsername");
        Move(obj, "oAuthTokenUrl", pull, "oauthTokenUrl");
        Move(obj, "oAuthClientId", pull, "oauthClientId");
        Move(obj, "oAuthScopes", pull, "oauthScope");

        // The credential is one value in v2 and two in v3: the location is folded out of the
        // enum member, and the header and query parameter names collapse into one field.
        var authType = Take(obj, "authType")?.GetValue<string>();
        var headerName = Take(obj, "authHeaderName");
        var queryName = Take(obj, "authQueryParamName");

        if (authType is not null)
        {
            var isQuery = authType.Equals("ApiKeyQuery", StringComparison.OrdinalIgnoreCase);

            pull["authType"] = authType switch
            {
                var a when a.StartsWith("ApiKey", StringComparison.OrdinalIgnoreCase) => "ApiKey",
                _ => authType,
            };
            pull["authHeaderLocation"] = isQuery ? "query" : "header";
        }

        if ((headerName ?? queryName) is { } name)
            pull["authHeaderName"] = name;

        if (hasBlock) return true;

        obj["pull"] = pull;
        return true;
    }

    private static bool MigrateScript(JsonObject obj)
    {
        string[] markers =
        [
            "allowedOrigins", "formContainerId", "captchaProvider", "minSubmitDelaySeconds",
            "honeypotFieldName", "formFields", "submitButtonLabel",
        ];
        if (!markers.Any(m => Key(obj, m) is not null)) return false;

        var hasScript = Key(obj, "script") is not null;
        var hasHostedForm = Key(obj, "hostedForm") is not null;

        var script = new JsonObject();
        Move(obj, "allowedOrigins", script, "allowedOrigins");
        Move(obj, "formContainerId", script, "formSelector");
        Move(obj, "minSubmitDelaySeconds", script, "minFillTimeSeconds");

        var redirectUrl = Take(obj, "redirectUrl");
        if (redirectUrl is not null)
        {
            script["redirectUrl"] = redirectUrl;
            // v2 expressed "redirect after submit" by the presence of the URL alone.
            script["afterSubmit"] = string.IsNullOrWhiteSpace(redirectUrl.GetValue<string>())
                ? "message"
                : "redirect";
        }

        // v2 stored null for "no captcha"; v3 names that case.
        var captcha = Take(obj, "captchaProvider")?.GetValue<string>();
        script["captchaProvider"] = string.IsNullOrWhiteSpace(captcha) ? "None" : captcha;

        // v2 stored the honeypot FIELD NAME, v3 a boolean — the name is the SDK's to choose.
        var honeypot = Take(obj, "honeypotFieldName")?.GetValue<string>();
        script["honeypot"] = !string.IsNullOrWhiteSpace(honeypot);

        var hostedForm = new JsonObject();
        Move(obj, "consentText", hostedForm, "consentText");
        Move(obj, "consentVersion", hostedForm, "consentVersion");
        Move(obj, "submitButtonLabel", hostedForm, "submitLabel");
        Move(obj, "theme", hostedForm, "theme");

        // `order` disappears: v3 reads the field order off the array, and the endpoint that
        // serves the SDK re-numbers from the position.
        if (Take(obj, "formFields") is JsonArray fields)
        {
            foreach (var field in fields)
                (field as JsonObject)?.Remove(Key(field as JsonObject, "order") ?? "order");

            hostedForm["fields"] = fields.DeepClone();
        }

        if (!hasScript) obj["script"] = script;
        if (!hasHostedForm && hostedForm.Count > 0) obj["hostedForm"] = hostedForm;

        return true;
    }

    /// <summary>The object's own spelling of a key, matched case-insensitively, or null.</summary>
    private static string? Key(JsonObject? obj, string name)
        => obj?.Select(p => p.Key)
              .FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Detaches a value from the object, so it can be re-parented.</summary>
    private static JsonNode? Take(JsonObject obj, string name)
    {
        var key = Key(obj, name);
        if (key is null) return null;

        var value = obj[key]?.DeepClone();
        obj.Remove(key);
        return value;
    }

    private static void Move(JsonObject from, string fromName, JsonObject to, string toName)
    {
        if (Take(from, fromName) is { } value && Key(to, toName) is null)
            to[toName] = value;
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
