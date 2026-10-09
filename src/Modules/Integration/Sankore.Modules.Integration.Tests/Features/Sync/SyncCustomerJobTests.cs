namespace Sankore.Modules.Integration.Tests.Features.Sync;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Xunit;

/// <summary>
/// INT-20 criterion 4's second half: the webhook triggers a TARGETED synchronisation, and
/// criterion 3 still governs which customers exist as far as this module is concerned.
/// </summary>
public sealed class SyncCustomerJobTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-5555-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-6666-0000-0000-000000000002");

    [Fact]
    public async Task Projects_the_customer_the_external_identifier_resolves_to()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var crmId = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantA, connection.Id, crmId, "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var synced = await Run(db, projector, connection.Id, "CBS-0001");

        synced.Should().BeTrue();
        projector.Projected.Should().BeEquivalentTo([(TenantA, connection.Id, crmId)]);

        var reference = await db.References
            .IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.TenantId == TenantA);

        reference.LastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Does_nothing_for_an_external_identifier_that_is_not_referenced()
    {
        // Criterion 3 again, and here it is also the authorisation check: the external id arrived
        // in a webhook body, which is remote input. An id we hold no reference for is a customer
        // the CRM has never exchanged with this system, and there is nothing to refresh.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var synced = await Run(db, projector, connection.Id, "CBS-9999");

        synced.Should().BeFalse();
        projector.Projected.Should().BeEmpty();
    }

    [Fact]
    public async Task Cannot_reach_another_tenants_customer_through_the_same_external_identifier()
    {
        // External identifiers are assigned by the external system and two IMFs legitimately hold
        // the same string. A signed webhook on one connection must not be able to name the other
        // tenant's customer, which is why the lookup is scoped to tenant AND connection.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connectionA = SyncTestContext.SeedConnection(db, TenantA);
        var connectionB = SyncTestContext.SeedConnection(db, TenantB, name: "CBS du tenant B");

        var theirs = Guid.NewGuid();
        SyncTestContext.SeedReference(db, TenantB, connectionB.Id, theirs, "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var synced = await Run(db, projector, connectionA.Id, "CBS-0001");

        synced.Should().BeFalse();
        projector.Projected.Should().BeEmpty();
    }

    [Fact]
    public async Task Cannot_reach_a_customer_referenced_through_another_connection()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        var other = SyncTestContext.SeedConnection(db, TenantA, name: "Autre CBS");

        SyncTestContext.SeedReference(db, TenantA, other.Id, Guid.NewGuid(), "CBS-0001");

        var projector = new SyncTestContext.RecordingProjector();

        var synced = await Run(db, projector, connection.Id, "CBS-0001");

        synced.Should().BeFalse();
        projector.Projected.Should().BeEmpty();
    }

    [Fact]
    public async Task An_account_reference_is_not_a_customer()
    {
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(
            db, TenantA, connection.Id, Guid.NewGuid(), "ACC-0001",
            entityType: IntegrationEntityTypes.Account);

        var projector = new SyncTestContext.RecordingProjector();

        (await Run(db, projector, connection.Id, "ACC-0001")).Should().BeFalse();
        projector.Projected.Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_touch_the_stream_cursor()
    {
        // Refreshing one customer says nothing about how far the stream has been read. Advancing
        // the cursor here would declare a window synchronised that nobody looked at — criterion
        // 2's failure mode, reached from the other side.
        await using var db = SyncTestContext.NewDb(TenantA);

        var connection = SyncTestContext.SeedConnection(db, TenantA);
        SyncTestContext.SeedReference(db, TenantA, connection.Id, Guid.NewGuid(), "CBS-0001");

        const string Window = "2026-04-01T08:00:00.0000000+00:00";
        SyncTestContext.SeedCursor(
            db, TenantA, connection.Id, SyncStream.Customers,
            lastRunAt: SyncTestContext.Now.AddHours(-3), cursor: Window);

        await Run(db, new SyncTestContext.RecordingProjector(), connection.Id, "CBS-0001");

        var cursor = SyncTestContext.ReadCursor(db, TenantA, connection.Id, SyncStream.Customers);

        cursor!.Cursor.Should().Be(Window);
        cursor.LastRunAt.Should().Be(SyncTestContext.Now.AddHours(-3));
    }

    private static Task<bool> Run(
        IntegrationDbContext db,
        ICbsSnapshotProjector projector,
        Guid connectionId,
        string externalId,
        Guid? tenantId = null)
        => SyncCustomerJob.RunAsync(
            db,
            projector,
            new SyncTestContext.FixedClock(SyncTestContext.Now),
            NullLogger<SyncCustomerJobTests>.Instance,
            tenantId ?? TenantA,
            connectionId,
            externalId,
            CancellationToken.None);
}
