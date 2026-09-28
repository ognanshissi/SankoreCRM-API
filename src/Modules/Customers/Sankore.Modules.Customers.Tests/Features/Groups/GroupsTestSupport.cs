namespace Sankore.Modules.Customers.Tests.Features.Groups;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Collaborators specific to the Groups zone, on top of the module-wide doubles in
/// <c>TestSupport/</c>. Each zone keeps its own copy of the recording publisher so
/// that no test file depends on another zone's fixtures.
/// </summary>
internal sealed class RecordingGroupEventPublisher : IEventPublisher
{
    private readonly List<IIntegrationEvent> _published = [];

    public IReadOnlyList<IIntegrationEvent> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fails every <c>SaveChanges</c> with the unique-index violation PostgreSQL would
/// raise. The InMemory provider enforces no index at all, so this is the only way to
/// exercise the race branch of a handler (two concurrent writers, one loses on commit)
/// without a real database.
/// </summary>
internal sealed class UniqueViolationInterceptor(string indexName) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
        => throw new DbUpdateException(
            $"23505: duplicate key value violates unique constraint \"{indexName}\"");
}

internal static class GroupTestData
{
    /// <summary>
    /// A context over a NAMED InMemory database and an explicit tenant. Two contexts
    /// built with the same name but different tenants share the store while keeping
    /// their own query filters — which is what a cross-tenant isolation test needs
    /// (<see cref="TestCustomersDbContextFactory"/> gives one tenant per store).
    /// </summary>
    internal static CustomersDbContext Context(string databaseName, Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new CustomersDbContext(options, new FixedTenantContext(tenantId));
    }

    /// <summary>Same as <see cref="Context"/>, but every commit fails on <paramref name="indexName"/>.</summary>
    internal static CustomersDbContext FailingContext(string databaseName, Guid tenantId, string indexName)
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(new UniqueViolationInterceptor(indexName))
            .Options;

        return new CustomersDbContext(options, new FixedTenantContext(tenantId));
    }

    /// <summary>Seeds <paramref name="count"/> eligible (PendingKyc) individuals and returns them.</summary>
    internal static async Task<List<Client>> SeedClientsAsync(
        CustomersDbContext db, Guid tenantId, Guid agencyId, int count, string prefix = "M")
    {
        var clients = new List<Client>();

        for (var i = 1; i <= count; i++)
        {
            var client = TestClientFactory.Individual(
                tenantId, agencyId,
                first: $"{prefix}{i}",
                last: "Traoré",
                clientNumber: $"ABJ-2026-{i:D6}");

            db.Clients.Add(client);
            clients.Add(client);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return clients;
    }

    /// <summary>A client the group rules must refuse: archived, hence read-only.</summary>
    internal static Client ArchivedClient(Guid tenantId, Guid agencyId, Guid actor)
    {
        var client = TestClientFactory.Individual(
            tenantId, agencyId, first: "Archivé", last: "Koné", clientNumber: "ABJ-2026-009001");

        client.ApplyKycValidated(DateTimeOffset.UtcNow, actor);
        client.Archive("Client deceased", actor);
        return client;
    }

    /// <summary>A client whose KYC was refused.</summary>
    internal static Client KycRejectedClient(Guid tenantId, Guid agencyId, Guid actor)
    {
        var client = TestClientFactory.Individual(
            tenantId, agencyId, first: "Rejeté", last: "Bamba", clientNumber: "ABJ-2026-009002");

        client.ApplyKycRejected("Forged document", DateTimeOffset.UtcNow, actor);
        return client;
    }

    /// <summary>
    /// Builds a group and fills it through the aggregate's own <c>AddMember</c>, so a
    /// fixture can never encode a composition the domain would have refused.
    /// </summary>
    internal static ClientGroup GroupWith(
        Guid tenantId,
        Guid agencyId,
        GroupType type,
        string name,
        IEnumerable<(Guid ClientId, GroupOfficeRole Role)> members,
        Guid actor,
        int? activateWithMinSize = null)
    {
        var group = TestClientFactory.Group(tenantId, agencyId, type, name);

        foreach (var (clientId, role) in members)
            group.AddMember(clientId, role, DateTimeOffset.UtcNow, actor);

        // Opt-in: some tests need the group already Active to observe what happens
        // when it later falls under its minimum size.
        if (activateWithMinSize is not null)
            group.TryActivate(activateWithMinSize.Value);

        return group;
    }
}
