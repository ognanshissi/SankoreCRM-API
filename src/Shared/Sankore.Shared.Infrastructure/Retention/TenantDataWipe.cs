namespace Sankore.Shared.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// One module's part in emptying a TEST institution of its business data (data-purge lot 4). The
/// platform requested it, a platform administrator retyped the institution's name, and the API
/// checked again that it is a test one before calling this.
/// </summary>
public interface ITenantDataWipe
{
    string Module { get; }

    /// <returns>Rows deleted (files are not counted).</returns>
    Task<int> WipeAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Deletes every row of one tenant in a module's schema, EXCEPT its configuration — read from the
/// EF model rather than listed by hand, so a business table added later is wiped by default and
/// keeping one is a decision written in <paramref name="keep"/>.
///
/// <para>
/// A table with a <c>TenantId</c> column loses that tenant's rows; one without it but pointing at
/// a tenant's table (a join table, a child without its own column) loses the rows hanging from the
/// tenant's; anything else (the outbox, platform tables) is left alone. Dependents are deleted
/// before what they point at, from the model's foreign keys. Owned types go with their owner —
/// their foreign key cascades. One transaction per module: all its tables or none.
/// </para>
/// </summary>
public static class TenantRows
{
    public static async Task<int> WipeAsync(
        DbContext db, Guid tenantId, IReadOnlyCollection<Type> keep, ILogger logger, CancellationToken ct)
    {
        var tables = PlanFor(db.Model, keep);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var total = 0;
        foreach (var table in tables)
        {
            if (table.Sql is null)
            {
                logger.LogWarning("Tenant wipe: {Table} kept — neither a tenant column nor a foreign key to one.", table.Name);
                continue;
            }
            total += await db.Database.ExecuteSqlRawAsync(table.Sql, [tenantId], ct);
        }
        await transaction.CommitAsync(ct);
        return total;
    }

    /// <param name="Sql">The DELETE, the tenant as parameter <c>{0}</c>; null when the table is left alone.</param>
    public sealed record PlannedTable(string Name, string? Sql, IEntityType Entity);

    /// <summary>
    /// The DELETE statements, dependents first. Public for the modules' tests: each checks its own
    /// plan (no kept table in it, children before parents) against its real model.
    /// </summary>
    public static IReadOnlyList<PlannedTable> PlanFor(IModel model, IReadOnlyCollection<Type> keep)
    {
        var entities = model.GetEntityTypes()
            // The outbox is every module's plumbing, without a tenant column: never part of a wipe.
            .Where(e => !e.IsOwned() && e.GetTableName() is not null && e.GetViewName() is null
                        && e.BaseType is null && !keep.Contains(e.ClrType) && e.ClrType != typeof(Outbox.OutboxMessage))
            .GroupBy(e => (e.GetSchema(), e.GetTableName()!))
            .Select(g => g.First())
            .ToList();

        var byType = entities.ToDictionary(e => (IReadOnlyEntityType)e);

        // referencedBy[P] = the tables pointing at P: they go before P.
        var referencedBy = entities.ToDictionary(e => e, _ => new HashSet<IEntityType>());
        foreach (var dependent in entities)
            foreach (var fk in dependent.GetForeignKeys())
                if (byType.TryGetValue(fk.PrincipalEntityType.GetRootType(), out var principal) && principal != dependent)
                    referencedBy[principal].Add(dependent);

        var ordered = new List<IEntityType>();
        var done = new HashSet<IEntityType>();
        while (done.Count < entities.Count)
        {
            var ready = entities.Where(e => !done.Contains(e) && referencedBy[e].All(done.Contains)).ToList();
            // A cycle: break it in model order — the FKs of a cycle are expected to cascade.
            if (ready.Count == 0) ready = [entities.First(e => !done.Contains(e))];
            foreach (var e in ready) { ordered.Add(e); done.Add(e); }
        }

        return ordered.Select(e => new PlannedTable(Qualified(e), SqlFor(e), e)).ToList();
    }

    /// <summary>
    /// What is wrong with a plan, empty when nothing: a kept type in it, a table left without a
    /// statement, a child deleted after its parent. For the modules' tests.
    /// </summary>
    public static IReadOnlyList<string> Problems(IModel model, IReadOnlyCollection<Type> keep)
    {
        var plan = PlanFor(model, keep);
        var problems = new List<string>();
        var position = plan.Select((t, i) => (t.Entity, i)).ToDictionary(x => x.Entity, x => x.i);

        foreach (var table in plan)
        {
            if (keep.Contains(table.Entity.ClrType)) problems.Add($"{table.Name} is kept but planned");
            if (table.Sql is null) problems.Add($"{table.Name} has no tenant column and no foreign key to one");
        }

        foreach (var table in plan)
            foreach (var fk in table.Entity.GetForeignKeys())
                if (position.TryGetValue(fk.PrincipalEntityType.GetRootType(), out var parent)
                    && fk.PrincipalEntityType.GetRootType() != table.Entity
                    && position[table.Entity] > parent)
                    problems.Add($"{table.Name} is deleted after {fk.PrincipalEntityType.GetTableName()}, which it points at");

        return problems;
    }

    private static string? SqlFor(IEntityType entity)
    {
        var table = Store(entity);
        var tenant = entity.FindProperty("TenantId");
        if (tenant is not null)
            return $"DELETE FROM {Qualified(entity)} WHERE {Q(tenant.GetColumnName(table)!)} = {{0}}";

        // No column of its own: the rows hanging from a tenant's table.
        foreach (var fk in entity.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType;
            var principalTenant = principal.FindProperty("TenantId");
            if (principalTenant is null || fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1) continue;

            var principalTable = Store(principal);
            return $"DELETE FROM {Qualified(entity)} WHERE {Q(fk.Properties[0].GetColumnName(table)!)} IN "
                 + $"(SELECT {Q(fk.PrincipalKey.Properties[0].GetColumnName(principalTable)!)} FROM {Qualified(principal)} "
                 + $"WHERE {Q(principalTenant.GetColumnName(principalTable)!)} = {{0}})";
        }
        return null;
    }

    private static StoreObjectIdentifier Store(IReadOnlyEntityType e) =>
        StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());

    private static string Qualified(IReadOnlyEntityType e) =>
        e.GetSchema() is { } schema ? $"{Q(schema)}.{Q(e.GetTableName()!)}" : Q(e.GetTableName()!);

    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

/// <summary>
/// A tenant's files in the object stores whose keys start with the tenant's token —
/// <c>sha256(tenantId)[..16]</c>, the prefix every tenant-scoped store uses (KYC documents,
/// recordings, claim documents, batch files, task attachments, WhatsApp media).
/// </summary>
public static class TenantObjects
{
    public static string Token(Guid tenantId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    /// <returns>Objects deleted. A concern not registered on this host is skipped.</returns>
    public static async Task<int> WipeAsync(
        IServiceProvider services, Guid tenantId, IEnumerable<string> concerns, ILogger logger, CancellationToken ct)
    {
        var prefix = Token(tenantId) + "/";
        var deleted = 0;
        foreach (var concern in concerns)
        {
            if (services.GetKeyedService<IObjectBackend>(concern) is not { } backend) continue;

            // Listed first, then deleted: deleting while a bucket listing pages is undefined.
            var keys = new List<string>();
            await foreach (var key in backend.ListAsync(prefix, ct)) keys.Add(key);

            foreach (var key in keys)
            {
                try
                {
                    if (await backend.DeleteAsync(key, ct)) deleted++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Tenant wipe: object {Key} of {Concern} could not be deleted ({Reason}).",
                        key, concern, ex.GetType().Name);
                }
            }
        }
        return deleted;
    }
}
