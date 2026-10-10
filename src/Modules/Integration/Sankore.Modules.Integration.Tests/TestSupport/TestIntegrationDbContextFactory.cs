namespace Sankore.Modules.Integration.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates <see cref="IntegrationDbContext"/> instances over the EF Core InMemory provider, all
/// sharing one named database so several contexts inside a single test see the same rows — the
/// way several requests do against PostgreSQL in production.
///
/// <para>
/// Each context is bound to a real <see cref="FixedTenantContext"/> rather than a bypass, so the
/// module's global query filters are genuinely exercised: a handler that forgets its tenant
/// predicate still fails here.
/// </para>
///
/// <para>
/// <see cref="ContextFor"/> hands back a context bound to a DIFFERENT tenant over the SAME
/// physical database. That is what makes a two-tenant isolation test possible: tenant B's rows
/// are seeded through a tenant-A context (EF query filters apply to reads, not to inserts) and
/// then read back through a B context to prove the emptiness above is isolation and not a broken
/// query.
/// </para>
///
/// <para>
/// Caveat to keep in mind when writing assertions: the InMemory provider enforces neither unique
/// indexes nor filtered ones. Nothing here can prove the uniqueness of
/// <c>ux_integration_connection_active_core_banking</c> — that belongs to the migration, and to
/// the handler's explicit pre-check which is what these tests do cover.
/// </para>
/// </summary>
public sealed class TestIntegrationDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"integration-tests-{Guid.NewGuid()}";

    public Guid TenantId { get; } = tenantId;

    public IntegrationDbContext CreateContext() => ContextFor(TenantId);

    /// <summary>
    /// A context bound to <paramref name="otherTenantId"/> over the same database. The database
    /// name is deliberately shared: a second factory would be a second store, and a cross-tenant
    /// test over two stores proves nothing.
    /// </summary>
    public IntegrationDbContext ContextFor(Guid otherTenantId)
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        ITenantContext tenantContext = new FixedTenantContext(otherTenantId);

        return new IntegrationDbContext(options, tenantContext);
    }

    public void Dispose()
    {
        // InMemory databases are collected with the last context that used them; the method is
        // kept so tests can adopt `using` now and a container-backed factory can add real
        // teardown later without touching them.
    }
}
