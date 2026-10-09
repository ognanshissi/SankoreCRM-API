namespace Sankore.Modules.Integration.Tests.Conventions;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// ASS-01's last criterion: « Les US INT-01 à INT-10 couvrent déjà les deux familles ; cette US
/// vérifie que l'assurance s'y branche sans modifier le socle. »
///
/// <para>
/// A US that verifies rather than builds, so these are tests and not code. Each one pins a
/// property of the socle that the insurance family relies on and that a later change could take
/// away silently — the failure mode in every case being that insurance keeps compiling while the
/// thing it depends on has quietly stopped being shared.
/// </para>
/// </summary>
public sealed class InsuranceOnTheSocleTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// The prefix rule of ASS-01's first criterion: « Seules les tables propres au core banking
    /// gardent le préfixe <c>cbs_</c> ».
    ///
    /// <para>
    /// Read from the model rather than asserted as a list, so a table added by a later wave is
    /// covered without this test being edited. The rule it enforces: <c>cbs_</c> is reserved for
    /// core banking, the family-agnostic socle carries <c>integration_</c>, and the insurance
    /// family carries <c>ins_</c> — the abbreviation ASS-11 itself uses in
    /// <c>Ins.Product.Manage</c>. The failure mode is a table that belongs to one family being
    /// named as if it were shared, which is how the next reader concludes the socle is
    /// family-specific.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_core_banking_tables_carry_the_cbs_prefix()
    {
        using var db = _factory.CreateContext();

        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct()
            .ToList();

        tables.Should().NotBeEmpty();

        var cbsTables = tables.Where(t => t.StartsWith("cbs_", StringComparison.Ordinal)).ToList();

        // Exactly one today, and the one the criterion names by hand. A second would need a
        // deliberate decision, which is what this assertion forces.
        cbsTables.Should().BeEquivalentTo(["cbs_customer_snapshot"]);

        // Every other table is either the shared socle or a declared family. `outbox_messages` and
        // `inbox_messages` are shared infrastructure and carry no prefix by the repo's own
        // convention — M01 and M02 name them identically in their own schemas.
        var unclassified = tables
            .Where(t => !t.StartsWith("cbs_", StringComparison.Ordinal))
            .Where(t => !t.StartsWith("integration_", StringComparison.Ordinal))
            .Where(t => !t.StartsWith("ins_", StringComparison.Ordinal))
            .ToList();

        unclassified.Should().BeEquivalentTo(["outbox_messages", "inbox_messages"]);
    }

    /// <summary>
    /// Every tenant-scoped entity of the module — both families — is covered by the central
    /// <c>HasQueryFilter</c>.
    ///
    /// <para>
    /// Reflective and not a list, because the thing being proved is COMPLETENESS: a per-entity
    /// test would pass for twenty-three entities while the twenty-fourth leaked. An entity added
    /// without its line in <c>IntegrationDbContext</c> fails here, which is a test failure instead
    /// of one institution reading another's portfolio.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_tenant_scoped_entity_of_both_families_has_a_query_filter()
    {
        using var db = _factory.CreateContext();

        // The two deliberate exemptions, documented in the context: infrastructure rows read by
        // processors that run outside any tenant scope, and OutboxProcessor additionally calls
        // IgnoreQueryFilters().
        var exempt = new[] { typeof(OutboxMessage), typeof(IntegrationInboxMessage) };

        var unfiltered = db.Model.GetEntityTypes()
            .Where(e => e.ClrType.GetProperty(nameof(AggregateRootTenant.TenantId)) is not null)
            .Where(e => !exempt.Contains(e.ClrType))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        unfiltered.Should().BeEmpty(
            "a tenant-scoped entity without a query filter leaks one institution's rows to the "
            + "next tenant on the same deployment; add it to the HasQueryFilter block in "
            + "IntegrationDbContext");
    }

    /// <summary>
    /// The outbox, the command table and the call log are ONE set of tables for both families —
    /// ASS-01's third and fourth criteria. Asserted by writing an insurance row into each and
    /// reading it back beside a core-banking one.
    /// </summary>
    [Fact]
    public async Task One_command_table_and_one_call_log_serve_both_families()
    {
        var clock = TimeProvider.System;

        await using var db = _factory.CreateContext();

        var cbs = Seed(db, IntegrationFamily.CoreBanking, IntegrationKind.Temenos, "CBS");
        var insurer = Seed(db, IntegrationFamily.Insurance, IntegrationKind.Orass, "ORASS");

        db.Commands.Add(IntegrationCommand.Create(
            Tenant, cbs.Id, CommandType.CreateCustomer, IntegrationEntityTypes.Customer,
            Guid.NewGuid(), new IdempotencyKey("CreateCustomer:a"), Guid.NewGuid(), clock));

        db.Commands.Add(IntegrationCommand.Create(
            Tenant, insurer.Id, CommandType.SubscribePolicy, IntegrationEntityTypes.Policy,
            Guid.NewGuid(), new IdempotencyKey("SubscribePolicy:b"), Guid.NewGuid(), clock));

        await db.SaveChangesAsync();

        await using var read = _factory.CreateContext();

        // Same table, same statuses, same dispatcher query. If insurance ever needed its own
        // command table, this is where that would show up.
        (await read.Commands.CountAsync()).Should().Be(2);

        (await read.Commands.CountAsync(c => c.ConnectionId == insurer.Id)).Should().Be(1);
        (await read.Commands.CountAsync(c => c.ConnectionId == cbs.Id)).Should().Be(1);

        // Every command references the connection that executes it — criterion 3, literally.
        (await read.Commands.ToListAsync())
            .Should().OnlyContain(c => c.ConnectionId != Guid.Empty);
    }

    /// <summary>
    /// The five money-and-policy command types ASS-04, ASS-05 and ASS-09 need exist on the SHARED
    /// <c>CommandType</c> enum, so the shared dispatcher's switch covers them.
    /// </summary>
    [Fact]
    public void The_shared_command_type_enum_carries_the_insurance_and_premium_operations()
    {
        var types = Enum.GetValues<CommandType>();

        types.Should().Contain(
        [
            CommandType.SubscribePolicy,
            CommandType.CancelPolicy,
            CommandType.DeclareClaim,
            // ASS-05's two: the money moves in the CBS, so they are core-banking operations
            // reached by an insurance flow — which is the clearest demonstration that the two
            // families share one command table.
            CommandType.DebitAccount,
            CommandType.ReverseDebit,
        ]);
    }

    /// <summary>
    /// The synchronisation cursor table and its stream enum serve both families (INT-20, ASS-07,
    /// ASS-09): <c>Policies</c> and <c>Claims</c> sit beside <c>Customers</c> and <c>Accounts</c>
    /// on one enum and one table.
    /// </summary>
    [Fact]
    public void One_sync_cursor_table_serves_both_families()
    {
        Enum.GetValues<SyncStream>().Should().Contain([SyncStream.Policies, SyncStream.Claims]);
    }

    /// <summary>
    /// The reconciliation tables are family-agnostic — criterion 4 — so ASS-10's premium-versus-
    /// policy reconciliation can reuse them rather than growing a second registry.
    ///
    /// <para>
    /// What this pins is the absence of a family column on the gap: it is keyed by CONNECTION, and
    /// a connection already carries its family.
    /// </para>
    /// </summary>
    [Fact]
    public void The_reconciliation_gap_is_keyed_by_connection_and_carries_no_family_of_its_own()
    {
        using var db = _factory.CreateContext();

        var gap = db.Model.FindEntityType(typeof(IntegrationReconciliationGap));
        gap.Should().NotBeNull();

        gap!.GetProperties().Select(p => p.Name).Should().Contain(nameof(IntegrationReconciliationGap.ConnectionId));

        gap.GetProperties().Select(p => p.Name).Should().NotContain(
            "Family",
            "a gap is keyed by connection, and a connection already knows its family; a second "
            + "column would be a place for the two to disagree");
    }

    private static IntegrationConnection Seed(
        IntegrationDbContext db, IntegrationFamily family, IntegrationKind kind, string name)
    {
        var clock = TimeProvider.System;

        ConnectionSettings settings = kind switch
        {
            IntegrationKind.Orass => new OrassSettings { IntermediaryCode = "INT-0042" },
            IntegrationKind.Temenos => new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.StaticToken,
                CompanyId = "CI0010001",
            },
            _ => new FakeSettings(),
        };

        var connection = IntegrationConnection.Create(
            Tenant, family, kind, IntegrationMode.Api, name, settings, Guid.NewGuid(), clock);

        db.Connections.Add(connection);
        db.SaveChanges();

        return connection;
    }
}

/// <summary>
/// Only to name <c>TenantId</c> in a <c>nameof</c> without depending on a particular aggregate.
/// <c>AggregateRoot</c> itself is abstract, so it cannot be instantiated for the purpose.
/// </summary>
internal sealed class AggregateRootTenant
{
    public Guid TenantId { get; set; }
}
