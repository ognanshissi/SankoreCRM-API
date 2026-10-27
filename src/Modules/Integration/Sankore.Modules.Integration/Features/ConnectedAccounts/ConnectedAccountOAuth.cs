namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Deployment settings of the connected accounts (section <c>Integration:ConnectedAccounts</c>).
///
/// <para>
/// <b>One OAuth application per provider for the whole platform</b> (owner's decision): an Azure
/// multi-tenant app and a Google Cloud OAuth client, registered once, configured per deployment.
/// The secrets go through user-secrets or the environment, never a committed file.
/// </para>
/// </summary>
public sealed class ConnectedAccountOptions
{
    public const string Section = "Integration:ConnectedAccounts";

    /// <summary>
    /// The API's public base URL, from which the redirect URI is built
    /// (<c>{PublicBaseUrl}/integration/oauth/{provider}/callback</c>) — it must be registered,
    /// to the character, at Microsoft and Google. Unset: built from the request the consent
    /// started with, which is right behind a proxy that forwards the original host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Development only: simulated providers for any provider with no client id, so the flow runs on a laptop.</summary>
    public bool UseFakeProviders { get; set; }

    public int StateLifetimeMinutes { get; set; } = 10;

    public MicrosoftOptions Microsoft { get; set; } = new();

    public GoogleOptions Google { get; set; } = new();

    public sealed class MicrosoftOptions
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }

        /// <summary><c>common</c>: work AND personal Microsoft accounts, which the screen asks for.</summary>
        public string Tenant { get; set; } = "common";
    }

    public sealed class GoogleOptions
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }
}

/// <summary>The OAuth plumbing shared by every provider: the state, its hash and the PKCE pair.</summary>
internal sealed class ConnectedAccountOAuth(IConfiguration config)
{
    /// <summary>Versioned: the string is part of the derivation, changing it invalidates every consent in flight.</summary>
    private const string VerifierInfo = "sankore:connected-account-pkce:v1";

    /// <summary>32 random bytes, base64url — what travels to the provider and back.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashState(string state) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    /// <summary>
    /// The PKCE verifier of a consent, DERIVED from its state rather than stored: HKDF over the
    /// platform key, so nobody reading the database can redeem an intercepted code. 43 characters,
    /// the minimum RFC 7636 allows and plenty of entropy (256 bits).
    /// </summary>
    public string CodeVerifier(string state)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(PlatformKey()),
            outputLength: 32,
            info: Encoding.UTF8.GetBytes($"{VerifierInfo}:{state}"));

        return Base64Url(key);
    }

    public static string CodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CallbackPath(ConnectedAccountProvider provider) =>
        $"/integration/oauth/{provider.ToString().ToLowerInvariant()}/callback";

    private string PlatformKey() =>
        config["Jwt:SigningKey"]
        ?? throw new InvalidOperationException("Jwt:SigningKey is required to derive the PKCE verifiers of connected accounts.");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class ConnectedAccountErrors
{
    public const string NotFound = "CONNECTED_ACCOUNT_NOT_FOUND";
    public const string ProviderUnavailable = "CONNECTED_ACCOUNT_PROVIDER_UNAVAILABLE";
    public const string OriginNotAllowed = "CONNECTED_ACCOUNT_ORIGIN_NOT_ALLOWED";
    public const string NeedsReconnect = "CONNECTED_ACCOUNT_NEEDS_RECONNECT";
    public const string ProviderError = "CONNECTED_ACCOUNT_PROVIDER_ERROR";
}

/// <summary>The vault keys of a connected account's tokens — the only place a token lives.</summary>
internal static class ConnectedAccountSecrets
{
    public const string Scope = "user-account";
    public const string RefreshTokenName = "refresh-token";
    public const string AccessTokenName = "access-token";

    /// <summary>The password of an IMAP/SMTP mailbox (connected mailbox) — usually an application password.</summary>
    public const string MailPasswordName = "mail-password";

    public static SecretKey RefreshToken(Guid tenantId, Guid accountId) => new(tenantId, Scope, accountId, RefreshTokenName);

    public static SecretKey AccessToken(Guid tenantId, Guid accountId) => new(tenantId, Scope, accountId, AccessTokenName);

    public static SecretKey MailPassword(Guid tenantId, Guid accountId) => new(tenantId, Scope, accountId, MailPasswordName);

    /// <summary>
    /// Runs a vault call OUTSIDE the ambient transaction. The vault has its own DbContext, so
    /// inside a command's TransactionScope its connection would enlist next to this module's and
    /// promote the transaction to a distributed one — which Npgsql refuses at run time. Same rule
    /// as every cross-store read under a command in this repo.
    /// </summary>
    public static async Task<T> OutsideTransactionAsync<T>(Func<Task<T>> call)
    {
        using var suppressed = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

        var result = await call();
        suppressed.Complete();
        return result;
    }

    public static Task OutsideTransactionAsync(Func<Task> call) =>
        OutsideTransactionAsync(async () => { await call(); return true; });
}
