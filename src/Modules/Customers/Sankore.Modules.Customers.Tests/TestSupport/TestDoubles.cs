namespace Sankore.Modules.Customers.Tests.TestSupport;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Shared collaborators for M01 handler tests.
///
/// The crypto doubles are hand-written rather than mocked on purpose: a substitute
/// returning <c>null</c> for Encrypt would make every "the value was encrypted before
/// persistence" assertion pass for the wrong reason. These fakes are reversible and
/// deterministic, so a test can assert on the stored value AND read it back.
/// </summary>
public static class TestDoubles
{
    /// <summary>Authenticated caller with the given identity and roles.</summary>
    public static ICurrentUser CurrentUser(Guid tenantId, Guid userId, params string[] roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(userId);
        user.TenantId.Returns(tenantId);
        user.IsAuthenticated.Returns(true);
        user.DisplayName.Returns("test-user");
        user.Roles.Returns(roles.ToList());
        return user;
    }

    /// <summary>
    /// Reversible clear-text "encryption": <c>"enc:" + value</c>. A test can therefore
    /// assert both that a column is not the clear value and that the round trip works.
    /// </summary>
    public static IFieldEncryptor Encryptor() => new FakeFieldEncryptor();

    /// <summary>
    /// Keyless but deterministic blind index: SHA-256 over <c>purpose + ":" + normalized</c>,
    /// reusing the production <see cref="SensitiveValueNormalizer"/> so that the tests
    /// exercise the real normalization rules (a phone typed with spaces and one typed
    /// with an indicative must collide here exactly as they do in production).
    /// </summary>
    public static IBlindIndexer Indexer() => new FakeBlindIndexer();

    /// <summary>
    /// In-memory settings: the declared defaults, with <paramref name="overrides"/>
    /// applied on top. No database, so it is safe to use in pure domain tests.
    /// </summary>
    public static ICustomerSettings Settings(Guid tenantId, params (string Key, string Value)[] overrides)
        => new FakeCustomerSettings(overrides);

    /// <summary>
    /// Agency perimeter. Passing no id yields the unrestricted (super-user) case, where
    /// <c>GetAccessibleAgencyIdsAsync</c> returns <c>null</c> and every agency is allowed —
    /// which is what most handler tests want. Pass ids to test the restricted path.
    /// </summary>
    public static IAgencyScopeProvider AgencyScope(params Guid[] accessibleAgencyIds)
        => new FakeAgencyScopeProvider(accessibleAgencyIds);

    // ─────────────────────────────────────────────────────────────────────────

    private sealed class FakeFieldEncryptor : IFieldEncryptor
    {
        private const string Prefix = "enc:";

        public string? Encrypt(string? plaintext) => plaintext is null ? null : Prefix + plaintext;

        public string? Decrypt(string? ciphertext) => ciphertext is null
            ? null
            : ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
                ? ciphertext[Prefix.Length..]
                // Tolerate a value that was stored without going through Encrypt so a
                // test seeding raw text does not blow up on read.
                : ciphertext;
    }

    private sealed class FakeBlindIndexer : IBlindIndexer
    {
        public string Compute(BlindIndexPurpose purpose, string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var normalized = purpose switch
            {
                BlindIndexPurpose.Phone => SensitiveValueNormalizer.NormalizePhone(value),
                BlindIndexPurpose.Email => SensitiveValueNormalizer.NormalizeEmail(value),
                BlindIndexPurpose.IdentityDocument => SensitiveValueNormalizer.NormalizeDocumentNumber(value),
                BlindIndexPurpose.RegistrationNumber => SensitiveValueNormalizer.NormalizeDocumentNumber(value),
                BlindIndexPurpose.PostalAddress => SensitiveValueNormalizer.NormalizeAddress(value),
                // Dates already arrive as "yyyy-MM-dd" from the caller.
                BlindIndexPurpose.DateOfBirth => value.Trim(),
                _ => value.Trim(),
            };

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{normalized}"));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed class FakeCustomerSettings : ICustomerSettings
    {
        private readonly Dictionary<string, string> _values;

        internal FakeCustomerSettings((string Key, string Value)[] overrides)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var declared in CustomerSettingKeys.Defaults)
                _values[declared.Key] = declared.Value;
            foreach (var (key, value) in overrides)
                _values[key] = value;
        }

        public Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct)
            => Task.FromResult(Read(key));

        public Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct)
            => Task.FromResult(int.Parse(Read(key), NumberStyles.Integer, CultureInfo.InvariantCulture));

        public Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct)
            => Task.FromResult(bool.Parse(Read(key)));

        public Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct)
            => Task.FromResult(decimal.Parse(Read(key), NumberStyles.Number, CultureInfo.InvariantCulture));

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(_values, StringComparer.Ordinal));

        public Task<Result> SetAsync(Guid tenantId, string key, string value, Guid actor, CancellationToken ct)
        {
            if (!CustomerSettingKeys.DefaultsByKey.ContainsKey(key))
                return Task.FromResult(Result.Fail(CustomerErrors.SettingUnknown));

            _values[key] = value;
            return Task.FromResult(Result.Ok());
        }

        private string Read(string key) => _values.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Unknown customer setting key '{key}' in test doubles.");
    }

    private sealed class FakeAgencyScopeProvider(Guid[] accessibleAgencyIds) : IAgencyScopeProvider
    {
        private readonly HashSet<Guid>? _accessible =
            accessibleAgencyIds.Length == 0 ? null : new HashSet<Guid>(accessibleAgencyIds);

        public Task<IReadOnlySet<Guid>?> GetAccessibleAgencyIdsAsync(Guid tenantId, Guid userId, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<Guid>?>(_accessible);

        public Task<bool> CanAccessAgencyAsync(Guid tenantId, Guid userId, Guid agencyId, CancellationToken ct)
            => Task.FromResult(_accessible is null || _accessible.Contains(agencyId));
    }
}
