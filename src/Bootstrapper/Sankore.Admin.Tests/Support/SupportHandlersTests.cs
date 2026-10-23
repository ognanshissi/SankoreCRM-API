namespace Sankore.Admin.Tests.Support;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Support;
using Sankore.Admin.Features.Support.PlatformSide;
using Sankore.Admin.Features.Support.TenantSide;
using Sankore.Admin.Infrastructure;
using Xunit;

/// <summary>
/// The circuit tenant ↔ platform as the registry's handlers enforce it. InMemory: the ticket
/// number is the database's identity column and stays 0 here, so no test reads it.
/// </summary>
public sealed class SupportHandlersTests : IDisposable
{
    private readonly string _name = Guid.NewGuid().ToString();
    private readonly Guid _tenantId;
    private readonly Guid _otherTenantId;
    private readonly Guid _adminId;
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    public SupportHandlersTests()
    {
        using var db = Create();
        var tenant = Tenant.Create("Caisse Avenir", "root@caisse.ci", "https://crm.caisse.ci");
        var other = Tenant.Create("Autre IMF", "root@autre.ci", "https://crm.autre.ci");
        var admin = PlatformAdmin.Create("ops@sankore.io", "Awa Support", "hash");
        db.Tenants.AddRange(tenant, other);
        db.PlatformAdmins.Add(admin);
        db.SaveChanges();
        _tenantId = tenant.Id;
        _otherTenantId = other.Id;
        _adminId = admin.Id;
    }

    public void Dispose()
    {
        using var db = Create();
        db.Database.EnsureDeleted();
    }

    private AdminDbContext Create() => new(new DbContextOptionsBuilder<AdminDbContext>()
        .UseInMemoryDatabase(_name)
        .Options);

    private async Task<Guid> OpenAsync(Guid? userId = null, string body = "La synchronisation CBS échoue depuis ce matin.")
    {
        await using var db = Create();
        var result = await new OpenSupportTicketHandler(db, NullLogger<OpenSupportTicketHandler>.Instance)
            .Handle(new OpenSupportTicketCommand(_tenantId, new OpenSupportTicketEndpoint.Request(
                userId ?? Requester, "agent@caisse.ci", "Koffi Agent", "Agent", "Synchronisation bloquée",
                SupportTicketCategory.Bug, SupportTicketPriority.High, body, "/integration/commands", "1.4.0", "Firefox")), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.TicketId!.Value;
    }

    private async Task<SupportResult> TenantAsync(Guid ticketId, TenantSupportAction action, string? body = null,
        Guid? userId = null, bool requesterOnly = true, Guid? tenantId = null)
    {
        await using var db = Create();
        return await new TenantSupportActionHandler(db, NullLogger<TenantSupportActionHandler>.Instance)
            .Handle(new TenantSupportActionCommand(tenantId ?? _tenantId, ticketId,
                new TenantSupportActorRequest(userId ?? Requester, "Koffi Agent", requesterOnly), action, body), default);
    }

    private async Task<SupportResult> PlatformAsync(Guid ticketId, PlatformSupportAction action,
        PlatformSupportReplyRequest? reply = null, SupportTicketStatus? status = null)
    {
        await using var db = Create();
        return await new PlatformSupportActionHandler(db, NullLogger<PlatformSupportActionHandler>.Instance)
            .Handle(new PlatformSupportActionCommand(ticketId, _adminId, action, reply, status, null, null), default);
    }

    private async Task<SupportTicket> TicketAsync(Guid id)
    {
        await using var db = Create();
        return await db.SupportTickets.AsNoTracking().Include(t => t.Messages).SingleAsync(t => t.Id == id);
    }

    private async Task<SupportTicketDetail?> TenantDetailAsync(Guid id, Guid? requester = null, Guid? tenantId = null)
    {
        await using var db = Create();
        return await new GetTenantSupportTicketHandler(db)
            .Handle(new GetTenantSupportTicketQuery(tenantId ?? _tenantId, id, requester), default);
    }

    [Fact]
    public async Task An_opened_ticket_is_new_and_carries_the_first_message()
    {
        var id = await OpenAsync();

        var ticket = await TicketAsync(id);
        ticket.Status.Should().Be(SupportTicketStatus.New);
        ticket.TenantId.Should().Be(_tenantId);
        ticket.PageUrl.Should().Be("/integration/commands");
        ticket.Messages.Should().ContainSingle()
            .Which.Should().Match<SupportMessage>(m => m.Kind == SupportMessageKind.Reply
                                                       && m.AuthorKind == SupportAuthorKind.Tenant
                                                       && m.AuthorId == Requester);
    }

    [Fact]
    public async Task A_blank_message_opens_nothing()
    {
        await using var db = Create();
        var result = await new OpenSupportTicketHandler(db, NullLogger<OpenSupportTicketHandler>.Instance)
            .Handle(new OpenSupportTicketCommand(_tenantId, new OpenSupportTicketEndpoint.Request(
                Requester, "agent@caisse.ci", "Koffi Agent", null, "Sujet", SupportTicketCategory.Question,
                SupportTicketPriority.Normal, "   ", null, null, null)), default);

        result.Error.Should().Be(SupportErrors.InvalidMessage);
        (await db.SupportTickets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_platform_reply_waits_on_the_tenant_and_the_tenant_answer_hands_it_back()
    {
        var id = await OpenAsync();

        (await PlatformAsync(id, PlatformSupportAction.Reply, new("Pouvez-vous nous donner l'heure exacte ?", false, null)))
            .IsSuccess.Should().BeTrue();
        var waiting = await TicketAsync(id);
        waiting.Status.Should().Be(SupportTicketStatus.WaitingOnTenant);
        waiting.AssignedAdminId.Should().Be(_adminId, "whoever answers first takes the ticket");

        (await TenantAsync(id, TenantSupportAction.Reply, "Vers 8 h 15.")).IsSuccess.Should().BeTrue();
        (await TicketAsync(id)).Status.Should().Be(SupportTicketStatus.InProgress);
    }

    [Fact]
    public async Task An_internal_note_never_reaches_the_tenant_but_the_platform_reads_it()
    {
        var id = await OpenAsync();
        await PlatformAsync(id, PlatformSupportAction.Reply, new("Voir le ticket ORASS-42 chez l'éditeur", true, null));

        var tenantView = await TenantDetailAsync(id);
        tenantView!.Messages.Should().NotContain(m => m.Kind == SupportMessageKind.InternalNote);
        tenantView.Ticket.Status.Should().Be(SupportTicketStatus.New, "a note changes nothing the tenant could notice");

        await using var db = Create();
        var platformView = await new GetPlatformSupportTicketHandler(db).Handle(new GetPlatformSupportTicketQuery(id), default);
        platformView!.Detail.Messages.Should().Contain(m => m.Kind == SupportMessageKind.InternalNote);
        platformView.TenantName.Should().Be("Caisse Avenir");
    }

    [Fact]
    public async Task A_user_without_the_wide_read_sees_and_touches_only_their_own_tickets()
    {
        var id = await OpenAsync();

        (await TenantDetailAsync(id, requester: Colleague)).Should().BeNull();
        (await TenantAsync(id, TenantSupportAction.Reply, "Moi aussi", userId: Colleague)).Error
            .Should().Be(SupportErrors.TicketNotFound);

        // The tenant administrator (no requester restriction) reads and answers it.
        (await TenantDetailAsync(id)).Should().NotBeNull();
        (await TenantAsync(id, TenantSupportAction.Reply, "Je confirme", userId: Colleague, requesterOnly: false))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenant_cannot_reach_the_ticket()
    {
        var id = await OpenAsync();

        (await TenantDetailAsync(id, tenantId: _otherTenantId)).Should().BeNull();
        (await TenantAsync(id, TenantSupportAction.Close, requesterOnly: false, tenantId: _otherTenantId)).Error
            .Should().Be(SupportErrors.TicketNotFound);
    }

    [Fact]
    public async Task A_closed_ticket_takes_no_reply_until_it_is_reopened()
    {
        var id = await OpenAsync();
        (await TenantAsync(id, TenantSupportAction.Close)).IsSuccess.Should().BeTrue();

        (await TenantAsync(id, TenantSupportAction.Reply, "Encore là")).Error.Should().Be(SupportErrors.TicketClosed);
        (await PlatformAsync(id, PlatformSupportAction.Reply, new("Réponse", false, null))).Error
            .Should().Be(SupportErrors.TicketClosed);

        (await TenantAsync(id, TenantSupportAction.Reopen)).IsSuccess.Should().BeTrue();
        (await TicketAsync(id)).Status.Should().Be(SupportTicketStatus.InProgress);
    }

    [Fact]
    public async Task Only_a_resolved_or_closed_ticket_can_be_reopened()
    {
        var id = await OpenAsync();

        (await TenantAsync(id, TenantSupportAction.Reopen)).Error.Should().Be(SupportErrors.InvalidTransition);
    }

    [Fact]
    public async Task A_status_change_is_written_into_the_thread()
    {
        var id = await OpenAsync();
        await PlatformAsync(id, PlatformSupportAction.Status, status: SupportTicketStatus.Resolved);

        var detail = await TenantDetailAsync(id);
        detail!.Ticket.Status.Should().Be(SupportTicketStatus.Resolved);
        detail.ResolvedAt.Should().NotBeNull();
        detail.Messages.Last().Should().Match<SupportMessageResponse>(m =>
            m.Kind == SupportMessageKind.StatusChange && m.Status == SupportTicketStatus.Resolved
                                                      && m.AuthorName == "Awa Support");
    }

    [Fact]
    public async Task A_resolved_ticket_closes_itself_after_a_week_without_answer_and_not_before()
    {
        var id = await OpenAsync();
        await PlatformAsync(id, PlatformSupportAction.Status, status: SupportTicketStatus.Resolved);
        var resolvedAt = (await TicketAsync(id)).ResolvedAt!.Value;

        await using (var db = Create())
            (await SupportAutoCloseService.CloseStaleAsync(db, resolvedAt.AddDays(6), default)).Should().Be(0);
        (await TicketAsync(id)).Status.Should().Be(SupportTicketStatus.Resolved);

        await using (var db = Create())
            (await SupportAutoCloseService.CloseStaleAsync(db, resolvedAt.AddDays(7), default)).Should().Be(1);
        var closed = await TicketAsync(id);
        closed.Status.Should().Be(SupportTicketStatus.Closed);
        closed.Messages.Last().AuthorKind.Should().Be(SupportAuthorKind.System);
    }

    [Fact]
    public async Task The_activity_feed_carries_the_platform_answers_only()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var id = await OpenAsync();
        await PlatformAsync(id, PlatformSupportAction.Reply, new("Note interne", true, null));
        await PlatformAsync(id, PlatformSupportAction.Reply, new("Nous regardons.", false, SupportTicketStatus.InProgress));
        await TenantAsync(id, TenantSupportAction.Reply, "Merci");

        await using var db = Create();
        var feed = await new ListTenantSupportActivityHandler(db)
            .Handle(new ListTenantSupportActivityQuery(_tenantId, since), default);

        feed.Should().ContainSingle(i => i.Kind == SupportMessageKind.Reply)
            .Which.Should().Match<SupportActivityItem>(i => i.AuthorName == "Awa Support" && i.RequesterUserId == Requester);
        feed.Should().ContainSingle(i => i.Kind == SupportMessageKind.StatusChange);
        feed.Should().NotContain(i => i.Kind == SupportMessageKind.InternalNote || i.AuthorKind == SupportAuthorKind.Tenant);
        (await new ListTenantSupportActivityHandler(db).Handle(new ListTenantSupportActivityQuery(_otherTenantId, since), default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenant_list_narrows_to_the_requester_when_asked()
    {
        await OpenAsync();
        await OpenAsync(Colleague);

        await using var db = Create();
        var handler = new ListTenantSupportTicketsHandler(db);
        (await handler.Handle(new ListTenantSupportTicketsQuery(_tenantId, Requester, null, false, null, null, null), default))
            .Total.Should().Be(1);
        (await handler.Handle(new ListTenantSupportTicketsQuery(_tenantId, null, null, false, null, null, null), default))
            .Total.Should().Be(2);
        (await handler.Handle(new ListTenantSupportTicketsQuery(_otherTenantId, null, null, false, null, null, null), default))
            .Total.Should().Be(0);
    }

    [Fact]
    public async Task A_ticket_cannot_be_given_to_an_unknown_administrator()
    {
        var id = await OpenAsync();

        await using var db = Create();
        var result = await new PlatformSupportActionHandler(db, NullLogger<PlatformSupportActionHandler>.Instance)
            .Handle(new PlatformSupportActionCommand(id, _adminId, PlatformSupportAction.Assign, null, null,
                new PlatformSupportAssignRequest(Guid.NewGuid()), null), default);

        result.Error.Should().Be(SupportErrors.AssigneeNotFound);
    }
}
