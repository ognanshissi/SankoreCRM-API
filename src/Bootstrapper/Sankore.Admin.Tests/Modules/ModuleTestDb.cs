namespace Sankore.Admin.Tests.Modules;

using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// An InMemory registry holding the two module definitions (priced and available, unlike the
/// migration's seed) and one tenant. InMemory enforces no filtered unique index, so these tests
/// cover the handlers' own checks; the index is the production guarantee behind them.
/// </summary>
internal sealed class ModuleTestDb : IDisposable
{
    public const decimal InsurancePrice = 150000m;
    public const decimal BankingPrice = 250000m;

    private readonly string _name = Guid.NewGuid().ToString();

    public Guid TenantId { get; }

    public ModuleTestDb()
    {
        using var db = Create();
        var now = DateTimeOffset.UtcNow;

        var insurance = ModuleDefinition.Create(TenantModules.Insurance, "Assurance", "Distribution d'assurance", now);
        insurance.Update("Assurance", "Distribution d'assurance", InsurancePrice, "XOF", isAvailable: true, now);
        var banking = ModuleDefinition.Create(TenantModules.BankingMicrofinance, "Banking - Microfinance", "", now);
        banking.Update("Banking - Microfinance", "", BankingPrice, "XOF", isAvailable: true, now);
        db.ModuleDefinitions.AddRange(insurance, banking);

        var tenant = Tenant.Create("Caisse Avenir", "root@caisse.ci", "https://crm.caisse.ci");
        db.Tenants.Add(tenant);
        TenantId = tenant.Id;

        db.SaveChanges();
    }

    public AdminDbContext Create() => new(new DbContextOptionsBuilder<AdminDbContext>()
        .UseInMemoryDatabase(_name)
        .Options);

    public void Dispose()
    {
        using var db = Create();
        db.Database.EnsureDeleted();
    }
}
