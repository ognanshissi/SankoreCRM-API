namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-34 criterion 5: the manual resolution, permissioned and audited.
/// </summary>
public sealed class ResolveReconciliationGapHandlerTests
{
    private static readonly Guid Officer = new("99999999-0000-0000-0000-000000000009");
    private static readonly Guid Customer = new("11111111-0000-0000-0000-000000000001");

    [Fact]
    public async Task Records_the_authenticated_operator_the_note_and_the_moment()
    {
        var databaseName = Guid.NewGuid().ToString();
        var gapId = await SeedGap(databaseName, ReconciliationTestContext.Tenant);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var result = await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "  Re-created in the CBS by hand.  "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Resolution.Should().Be(nameof(GapResolution.Resolved));
        result.Value.ResolvedBy.Should().Be(Officer);

        var gap = await db.ReconciliationGaps.AsNoTracking().SingleAsync();

        gap.Resolution.Should().Be(GapResolution.Resolved);
        gap.ResolvedBy.Should().Be(Officer);
        gap.ResolvedAt.Should().Be(ReconciliationTestContext.NightTwo);

        // Trimmed by the aggregate, which is also what makes a whitespace-only note fail its
        // own guard rather than be stored as a blank justification.
        gap.ResolutionNote.Should().Be("Re-created in the CBS by hand.");
    }

    [Fact]
    public async Task The_actor_is_the_authenticated_identity_and_not_anything_the_caller_sent()
    {
        // An audit trail anybody can write somebody else's name into is not an audit trail. The
        // command record carries no actor field at all, and this is what pins that: the only
        // place ResolvedBy can come from is ICurrentUser.
        typeof(ResolveReconciliationGapCommand)
            .GetProperties()
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["GapId", "Note", "ResourceType", "ResourceId"]);

        var databaseName = Guid.NewGuid().ToString();
        var gapId = await SeedGap(databaseName, ReconciliationTestContext.Tenant);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "Done."), CancellationToken.None);

        (await db.ReconciliationGaps.AsNoTracking().SingleAsync())
            .ResolvedBy.Should().Be(Officer);
    }

    [Fact]
    public async Task A_blank_note_is_refused_by_the_aggregate_even_if_the_validator_is_bypassed()
    {
        // The validator guards the HTTP path; this guards every other caller. An untraceable
        // resolution of a compliance finding is the one failure mode the table exists to prevent.
        var databaseName = Guid.NewGuid().ToString();
        var gapId = await SeedGap(databaseName, ReconciliationTestContext.Tenant);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var result = await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.PayloadInvalid);

        (await db.ReconciliationGaps.AsNoTracking().SingleAsync())
            .Resolution.Should().Be(GapResolution.Open);
    }

    [Fact]
    public async Task Resolving_twice_is_refused_rather_than_silently_overwriting_the_first_note()
    {
        var databaseName = Guid.NewGuid().ToString();
        var gapId = await SeedGap(databaseName, ReconciliationTestContext.Tenant);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "First."), CancellationToken.None);

        var second = await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "Second."), CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(IntegrationErrors.GapAlreadyResolved);

        (await db.ReconciliationGaps.AsNoTracking().SingleAsync())
            .ResolutionNote.Should().Be("First.");
    }

    [Fact]
    public async Task A_gap_of_another_tenant_reads_as_absent_and_never_as_forbidden()
    {
        // The house rule, and it matters more here than elsewhere: "forbidden" would confirm that
        // a divergence about one of another IMF's customers exists.
        var databaseName = Guid.NewGuid().ToString();
        var gapId = await SeedGap(databaseName, ReconciliationTestContext.OtherTenant);

        await using var db = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.Tenant, databaseName);

        var result = await Handler(db).Handle(
            new ResolveReconciliationGapCommand(gapId, "Done."), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.GapNotFound);

        // And the victim's row is untouched.
        await using var victim = ReconciliationTestContext.NewDb(
            ReconciliationTestContext.OtherTenant, databaseName);

        (await victim.ReconciliationGaps.AsNoTracking().SingleAsync())
            .Resolution.Should().Be(GapResolution.Open);
    }

    [Fact]
    public void The_command_is_audited_and_names_the_resource_it_closed()
    {
        // ICommand activates TransactionBehavior and AuditBehavior; IResourceCommand is what makes
        // the audit row filterable by the gap. Criterion 5 says "auditée", and these two markers
        // are the whole of how this repo spells that.
        var command = new ResolveReconciliationGapCommand(Guid.NewGuid(), "Done.");

        command.Should().BeAssignableTo<ICommand>();
        command.Should().BeAssignableTo<IResourceCommand>();
        command.ResourceType.Should().Be("IntegrationReconciliationGap");
        command.ResourceId.Should().Be(command.GapId.ToString());
    }

    private static ResolveReconciliationGapHandler Handler(IntegrationDbContext db)
        => new(
            db,
            ReconciliationTestContext.User(ReconciliationTestContext.Tenant, Officer),
            new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightTwo),
            NullLogger<ResolveReconciliationGapHandler>.Instance);

    private static async Task<Guid> SeedGap(string databaseName, Guid tenantId)
    {
        await using var db = ReconciliationTestContext.NewDb(tenantId, databaseName);

        var gap = IntegrationReconciliationGap.Open(
            tenantId: tenantId,
            runId: Guid.NewGuid(),
            connectionId: ReconciliationTestContext.ConnectionId,
            gapType: GapType.MissingInExternal,
            crmId: Customer,
            externalId: null,
            clock: new ReconciliationTestContext.FixedClock(ReconciliationTestContext.NightOne));

        db.ReconciliationGaps.Add(gap);
        await db.SaveChangesAsync();

        return gap.Id;
    }
}

/// <summary>The 422 shape of the manual resolution.</summary>
public sealed class ResolveReconciliationGapValidatorTests
{
    [Fact]
    public void A_missing_note_is_reported_under_the_json_property_the_caller_sent()
    {
        // OverridePropertyName and never WithName: the 422 body is keyed by the property name,
        // and WithName changes only the human message — the front then cannot attach the error to
        // its field, which is invisible until somebody tries to use the form.
        var result = new ResolveReconciliationGapValidator().Validate(
            new ResolveReconciliationGapCommand(Guid.NewGuid(), "  "));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "note");
    }

    [Fact]
    public void An_empty_gap_id_is_reported_under_gapId()
    {
        var result = new ResolveReconciliationGapValidator().Validate(
            new ResolveReconciliationGapCommand(Guid.Empty, "Done."));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "gapId");
    }

    [Fact]
    public void An_over_long_note_is_refused_before_the_column_truncates_it()
    {
        var result = new ResolveReconciliationGapValidator().Validate(
            new ResolveReconciliationGapCommand(Guid.NewGuid(), new string('x', 1001)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "note");
    }

    [Fact]
    public void A_well_formed_resolution_passes()
    {
        new ResolveReconciliationGapValidator()
            .Validate(new ResolveReconciliationGapCommand(Guid.NewGuid(), "Corrected in the CBS."))
            .IsValid.Should().BeTrue();
    }
}
