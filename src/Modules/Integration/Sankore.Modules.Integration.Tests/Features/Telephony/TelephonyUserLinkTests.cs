namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Linking a CRM user to a provider account, and taking it back (TEL-B-05).
/// </summary>
public sealed class TelephonyUserLinkTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyUserLinkTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_pairing_is_created_and_starts_active()
    {
        var result = await LinkAsync(_agent, "fake-user-1");

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var link = db.TelUserLinks.Single();

        link.CrmUserId.Should().Be(_agent);
        link.ExternalUserId.Should().Be("fake-user-1");
        link.Status.Should().Be(TelUserLinkStatus.Active);
        link.LinkedBy.Should().Be(TelephonyTestHarness.Actor);
    }

    [Fact]
    public async Task Confirming_the_same_pairing_twice_is_a_success_and_creates_one_row()
    {
        var first = await LinkAsync(_agent, "fake-user-1");
        var second = await LinkAsync(_agent, "fake-user-1");

        // A double-clicked confirm button must not read as the uniqueness rule firing.
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value);

        await using var db = _factory.CreateContext();
        db.TelUserLinks.Should().HaveCount(1);
    }

    [Fact]
    public async Task Another_user_cannot_take_an_account_that_is_already_linked()
    {
        await LinkAsync(_agent, "fake-user-1");

        var result = await LinkAsync(Guid.NewGuid(), "fake-user-1");

        // Silently moving the account would re-attribute every call that follows, with nothing in
        // the audit trail saying so.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TelephonyErrors.UserLinkAlreadyTaken);
    }

    [Fact]
    public async Task A_user_cannot_hold_a_second_account()
    {
        await LinkAsync(_agent, "fake-user-1");

        var result = await LinkAsync(_agent, "fake-user-2");

        result.Error.Should().Be(TelephonyErrors.UserLinkAlreadyTaken);
    }

    [Fact]
    public async Task Unlinking_disables_the_row_rather_than_deleting_it()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;

        var result = await UnlinkAsync(linkId, "L'agent a quitté l'institution.");

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var link = db.TelUserLinks.Single();

        // The agent's calls already in the journal point at this row, and it is what explains why
        // nothing new is attributed to them.
        link.Status.Should().Be(TelUserLinkStatus.Disabled);
        link.StatusReason.Should().Be("L'agent a quitté l'institution.");
    }

    [Fact]
    public async Task Unlinking_twice_is_a_success_and_keeps_the_first_reason()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;

        await UnlinkAsync(linkId, "Première raison.");
        var second = await UnlinkAsync(linkId, "Deuxième raison.");

        second.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();

        // The first explanation is the one a later reader needs; overwriting it would lose it.
        db.TelUserLinks.Single().StatusReason.Should().Be("Première raison.");
    }

    [Fact]
    public async Task Linking_the_same_pair_again_revives_the_unlinked_row()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;
        await UnlinkAsync(linkId, "Erreur de rapprochement.");

        var again = await LinkAsync(_agent, "fake-user-1");

        // Without the revival the unique index would answer "already taken" about a link the
        // screen shows as gone — unlinking is logical, so the row is still there.
        again.IsSuccess.Should().BeTrue();
        again.Value.Should().Be(linkId);

        await using var db = _factory.CreateContext();
        var link = db.TelUserLinks.Single();

        link.Status.Should().Be(TelUserLinkStatus.Active);
        link.StatusReason.Should().BeNull();
    }

    [Fact]
    public async Task Another_user_may_take_the_account_once_it_has_been_unlinked()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;
        await UnlinkAsync(linkId, "Compte recyclé par le fournisseur.");

        var result = await LinkAsync(Guid.NewGuid(), "fake-user-1");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_blank_unlink_reason_is_refused_by_its_own_code()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;

        var result = await UnlinkAsync(linkId, "   ");

        // Not "not found": an administrator sent to look for a row that is in front of them has
        // been told the wrong thing.
        result.Error.Should().Be(TelephonyErrors.UnlinkReasonRequired);
    }

    [Fact]
    public async Task The_caller_number_is_set_and_cleared_by_the_same_route()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;

        (await SetCallerNumberAsync(linkId, "+2252021000000")).IsSuccess.Should().BeTrue();

        await using (var db = _factory.CreateContext())
            db.TelUserLinks.Single().DefaultCallerNumber.Should().Be("+2252021000000");

        (await SetCallerNumberAsync(linkId, null)).IsSuccess.Should().BeTrue();

        await using (var db = _factory.CreateContext())
        {
            // Null is the ordinary state — fall back on the connection's own default — not an
            // incomplete one, which is why there is no separate reset route.
            db.TelUserLinks.Single().DefaultCallerNumber.Should().BeNull();
        }
    }

    [Fact]
    public async Task Setting_a_caller_number_on_a_link_that_does_not_exist_is_not_found()
    {
        var result = await SetCallerNumberAsync(Guid.NewGuid(), "+2252021000000");

        result.Error.Should().Be(TelephonyErrors.UserLinkNotFound);
    }

    [Fact]
    public async Task Another_tenants_link_is_not_found_rather_than_forbidden()
    {
        var linkId = (await LinkAsync(_agent, "fake-user-1")).Value;

        var other = Guid.NewGuid();
        await using var db = _factory.ContextFor(other);

        var result = await new SetTelephonyCallerNumberHandler(db).Handle(
            new SetTelephonyCallerNumberCommand(linkId, "+2252021000000"), CancellationToken.None);

        // The global query filter scopes it, so the existence of the row never leaks through a 403.
        result.Error.Should().Be(TelephonyErrors.UserLinkNotFound);
    }

    [Fact]
    public async Task Linking_is_refused_when_the_tenant_has_no_active_telephony_connection()
    {
        await using var db = _factory.CreateContext();

        // Deactivating the connection IS the switch — this module keeps no second one, and the
        // code an administrator reads says which screen to go to.
        // AsTracking: this DbContext is NoTracking by default — the module's handlers all say so
        // explicitly — and without it the deactivation below would be a no-op and this test would
        // pass against a connection that is still active.
        var connection = db.Connections.AsTracking().Single();
        connection.Deactivate(TelephonyTestHarness.Actor, TelephonyTestHarness.Clock);
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await LinkAsync(_agent, "fake-user-1");

        result.Error.Should().Be(TelephonyErrors.Disabled);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<Sankore.Shared.Kernel.Result<Guid>> LinkAsync(
        Guid crmUserId, string externalUserId)
    {
        await using var db = _factory.CreateContext();

        return await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(new LinkTelephonyUserCommand(crmUserId, externalUserId), CancellationToken.None);
    }

    private async Task<Sankore.Shared.Kernel.Result> UnlinkAsync(Guid linkId, string reason)
    {
        await using var db = _factory.CreateContext();

        return await new UnlinkTelephonyUserHandler(
                db, NullLogger<UnlinkTelephonyUserHandler>.Instance)
            .Handle(new UnlinkTelephonyUserCommand(linkId, reason), CancellationToken.None);
    }

    private async Task<Sankore.Shared.Kernel.Result> SetCallerNumberAsync(Guid linkId, string? number)
    {
        await using var db = _factory.CreateContext();

        return await new SetTelephonyCallerNumberHandler(db)
            .Handle(new SetTelephonyCallerNumberCommand(linkId, number), CancellationToken.None);
    }
}
