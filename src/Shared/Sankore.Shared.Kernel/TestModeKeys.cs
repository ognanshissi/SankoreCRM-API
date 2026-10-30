namespace Sankore.Shared.Kernel;

/// <summary>
/// The prefixes that say which mode a key belongs to (sandbox lot 3), Stripe-style: a partner
/// pasting a key sees at once whether it reaches production or « Mode test ». Generated keys only —
/// a key is resolved by its whole value, so the prefix decides nothing on the way in: a test key can
/// only ever reach the test twin that issued it. Keys issued before the prefixes keep working.
/// </summary>
public static class TestModeKeys
{
    public const string LivePublicKey = "pk_live_";
    public const string TestPublicKey = "pk_test_";
    public const string LiveWebhookSecret = "whsec_";
    public const string TestWebhookSecret = "whsec_test_";

    /// <summary>The public key prefix of a tenant: test for a twin or any tenant marked test.</summary>
    public static string PublicKeyPrefix(bool test) => test ? TestPublicKey : LivePublicKey;

    public static string WebhookSecretPrefix(bool test) => test ? TestWebhookSecret : LiveWebhookSecret;

    /// <summary>
    /// A key shown masked: its mode prefix in full — it is not secret, and it is what tells a test
    /// key from a live one on screen — then the first and last characters of the rest.
    /// </summary>
    public static string Mask(string value)
    {
        var prefix = new[] { TestWebhookSecret, LiveWebhookSecret, TestPublicKey, LivePublicKey }
            .FirstOrDefault(p => value.StartsWith(p, StringComparison.Ordinal)) ?? string.Empty;
        var rest = value[prefix.Length..];

        if (rest.Length <= 8) return prefix + "****";
        return prefix + (prefix.Length == 0 ? rest[..4] : string.Empty) + "****" + rest[^4..];
    }

    /// <summary>Whether <paramref name="tenantId"/> issues test keys. An unknown tenant issues live ones.</summary>
    public static async Task<bool> IsTestAsync(ITenantStore? store, Guid tenantId, CancellationToken ct)
        => store is not null && (await store.GetAsync(tenantId, ct))?.IsTest == true;
}
