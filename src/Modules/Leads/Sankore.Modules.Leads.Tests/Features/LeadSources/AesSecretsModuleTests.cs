namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Secrets;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class AesSecretsModuleTests : IDisposable
{
    private readonly string _dbName = $"secrets-tests-{Guid.NewGuid()}";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly IOptions<SecretsOptions> _options;

    public AesSecretsModuleTests()
    {
        var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _options = Options.Create(new SecretsOptions { EncryptionKey = encryptionKey });
    }

    public void Dispose() { }

    private SecretsDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<SecretsDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        var db = new SecretsDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private SecretKey Key(string name, Guid? entityId = null)
        => new(_tenantId, "LeadSource", entityId ?? Guid.NewGuid(), name);

    [Fact]
    public async Task Set_and_GetValue_round_trips()
    {
        var key = Key("hmac");
        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);

        await module.SetAsync(key, "my-secret-value");

        using var db2 = CreateDb();
        var module2 = new AesSecretsModule(db2, _options);
        var value = await module2.GetValueAsync(key);

        value.Should().Be("my-secret-value");
    }

    [Fact]
    public async Task GetHint_masks_value()
    {
        var key = Key("oauth");
        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);

        await module.SetAsync(key, "a]very-long-secret-value");

        using var db2 = CreateDb();
        var module2 = new AesSecretsModule(db2, _options);
        var hint = await module2.GetHintAsync(key);

        hint.Should().NotBeNull();
        hint!.Name.Should().Be("oauth");
        hint.MaskedValue.Should().Contain("****");
        hint.MaskedValue.Should().NotBe("a]very-long-secret-value");
    }

    [Fact]
    public async Task Set_overwrites_existing()
    {
        var key = Key("hmac");
        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);

        await module.SetAsync(key, "old");
        await module.SetAsync(key, "new");

        var value = await module.GetValueAsync(key);
        value.Should().Be("new");
    }

    [Fact]
    public async Task DeleteAll_removes_all_entries_for_entity()
    {
        var entityId = Guid.NewGuid();
        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);

        await module.SetAsync(Key("hmac", entityId), "v1");
        await module.SetAsync(Key("oauth", entityId), "v2");

        await module.DeleteAllAsync(_tenantId, "LeadSource", entityId);

        using var db2 = CreateDb();
        var module2 = new AesSecretsModule(db2, _options);
        (await module2.GetValueAsync(Key("hmac", entityId))).Should().BeNull();
        (await module2.GetValueAsync(Key("oauth", entityId))).Should().BeNull();
    }

    [Fact]
    public async Task GetValue_returns_null_for_missing_key()
    {
        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);

        var value = await module.GetValueAsync(Key("nope"));
        value.Should().BeNull();
    }

    [Fact]
    public async Task Set_with_expiresAt_stores_expiry()
    {
        var key = Key("temp");
        var expiry = DateTimeOffset.UtcNow.AddDays(7);

        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);
        await module.SetAsync(key, "value", expiry);

        var hint = await module.GetHintAsync(key);
        hint.Should().NotBeNull();
        hint!.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Different_tenant_cannot_read_secret()
    {
        var entityId = Guid.NewGuid();
        var key = new SecretKey(_tenantId, "LeadSource", entityId, "hmac");

        using var db = CreateDb();
        var module = new AesSecretsModule(db, _options);
        await module.SetAsync(key, "tenant-a-secret");

        // Different tenant tries to read
        var otherKey = new SecretKey(Guid.NewGuid(), "LeadSource", entityId, "hmac");
        using var db2 = CreateDb();
        var module2 = new AesSecretsModule(db2, _options);
        var value = await module2.GetValueAsync(otherKey);

        value.Should().BeNull();
    }
}
