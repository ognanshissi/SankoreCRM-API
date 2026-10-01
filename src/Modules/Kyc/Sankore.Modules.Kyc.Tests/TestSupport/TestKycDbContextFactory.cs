namespace Sankore.Modules.Kyc.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates <see cref="KycDbContext"/> instances on the EF InMemory provider, all sharing one named
/// database so data written through one context is visible to the next — the pattern every other
/// module's tests use. The context is bound to a fixed tenant so the module's global query filter
/// is genuinely exercised rather than bypassed.
/// </summary>
public sealed class TestKycDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"kyc-tests-{Guid.NewGuid()}";

    public KycDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KycDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        return new KycDbContext(options, new FixedTenantContext(tenantId));
    }

    public void Dispose() { /* InMemory — nothing to dispose */ }
}
