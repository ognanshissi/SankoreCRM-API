namespace Sankore.Modules.Leads.Tests.Features.GetMyAgenda;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetMyAgenda;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

public sealed class GetMyAgendaHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ILeadSettings _settings = Substitute.For<ILeadSettings>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public GetMyAgendaHandlerTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.SlaWarningThresholdPercent, Arg.Any<CancellationToken>())
            .Returns(75);
    }

    public void Dispose() => _db.Dispose();

    private Lead NewLead(string name) => Lead.Capture(
        tenantId: _tenantId,
        fullName: name,
        phoneNumber: "+2250708091801",
        source: LeadSource.Web,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "FR",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System);

    /// <summary>Un lead affecté à <paramref name="agentId"/>, SLA posé entre les deux instants.</summary>
    private (Lead, LeadAssignment) AssignedLead(string name, Guid agentId, DateTimeOffset createdAt, DateTimeOffset deadline)
    {
        var lead = NewLead(name);
        var assignment = LeadAssignment.Create(
            tenantId: _tenantId, leadId: lead.Id, agentId: agentId,
            strategy: DispatchingStrategy.RoundRobin, compatibilityScore: 42,
            slaDeadline: deadline, createdAt: createdAt);
        lead.AssignTo(assignment, currentAssignment: null);
        return (lead, assignment);
    }

    private async Task<MyAgendaDto> RunAsync(DateTimeOffset? from = null, int days = 14)
    {
        await using var db = _db.CreateContext();
        var handler = new GetMyAgendaHandler(db, _currentUser, _settings, _admin, _customers, TimeProvider.System);
        var result = await handler.Handle(new GetMyAgendaQuery(from ?? _now.AddHours(-1), days), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    [Fact]
    public async Task Reminders_are_mine_when_I_wrote_them_or_hold_the_lead()
    {
        var (mine, mineAssignment) = AssignedLead("Awa Ouattara", _me, _now.AddDays(-1), _now.AddDays(5));
        var (theirs, theirAssignment) = AssignedLead("Koffi Yao", _colleague, _now.AddDays(-1), _now.AddDays(5));

        await using (var db = _db.CreateContext())
        {
            db.Leads.AddRange(mine, theirs);
            db.LeadAssignments.AddRange(mineAssignment, theirAssignment);
            db.LeadReminders.AddRange(
                LeadReminder.Create(_tenantId, mine.Id, "Rappel posé par un collègue sur mon lead", _colleague, _now.AddHours(3)),
                LeadReminder.Create(_tenantId, theirs.Id, "Mon rappel sur le lead d'un collègue", _me, _now.AddHours(2)),
                LeadReminder.Create(_tenantId, theirs.Id, "Pas à moi", _colleague, _now.AddHours(1)),
                LeadReminder.Create(_tenantId, mine.Id, "Hors fenêtre", _me, _now.AddDays(30)),
                LeadReminder.Create(_tenantId, mine.Id, "En retard", _me, _now.AddDays(-2)));
            await db.SaveChangesAsync();
        }

        var agenda = await RunAsync();

        agenda.Reminders.Select(r => r.Title).Should().Equal(
            "En retard", "Mon rappel sur le lead d'un collègue", "Rappel posé par un collègue sur mon lead");
        agenda.Reminders[0].IsOverdue.Should().BeTrue();
        agenda.Reminders[0].LeadName.Should().Be("Awa Ouattara");
    }

    [Fact]
    public async Task Only_activities_scheduled_inside_the_window_are_listed()
    {
        var (lead, assignment) = AssignedLead("Awa Ouattara", _me, _now.AddDays(-1), _now.AddDays(5));

        await using (var db = _db.CreateContext())
        {
            db.Leads.Add(lead);
            db.LeadAssignments.Add(assignment);
            db.LeadActivities.AddRange(
                LeadActivity.Create(_tenantId, lead.Id, ActivityType.Meeting, "Rendez-vous agence", _me, scheduledAt: _now.AddDays(2)),
                LeadActivity.Create(_tenantId, lead.Id, ActivityType.Visit, "Visite boutique", _colleague, scheduledAt: _now.AddDays(1)),
                LeadActivity.Create(_tenantId, lead.Id, ActivityType.Call, "Appel passé, sans date prévue", _me),
                LeadActivity.Create(_tenantId, lead.Id, ActivityType.Meeting, "Rendez-vous d'hier", _me, scheduledAt: _now.AddDays(-1)),
                LeadActivity.Create(_tenantId, lead.Id, ActivityType.Meeting, "Dans un mois", _me, scheduledAt: _now.AddDays(30)));
            await db.SaveChangesAsync();
        }

        var agenda = await RunAsync();

        // La visite d'un collègue y figure : je détiens le lead.
        agenda.Activities.Select(a => a.Subject).Should().Equal("Visite boutique", "Rendez-vous agence");
    }

    [Fact]
    public async Task Sla_alerts_cover_my_open_assignments_breached_or_past_the_warning()
    {
        var (breached, a1) = AssignedLead("Dépassé", _me, _now.AddHours(-5), _now.AddHours(-1));
        // Fenêtre de 4 h, alerte à 75 % : à 3 h 30, l'alerte est passée.
        var (atRisk, a2) = AssignedLead("Menacé", _me, _now.AddHours(-3.5), _now.AddHours(0.5));
        var (calm, a3) = AssignedLead("Dans les délais", _me, _now.AddHours(-1), _now.AddHours(3));
        var (theirs, a4) = AssignedLead("Collègue", _colleague, _now.AddHours(-5), _now.AddHours(-1));

        await using (var db = _db.CreateContext())
        {
            db.Leads.AddRange(breached, atRisk, calm, theirs);
            db.LeadAssignments.AddRange(a1, a2, a3, a4);
            await db.SaveChangesAsync();
        }

        var agenda = await RunAsync();

        agenda.Alerts.Select(a => (a.Kind, a.LeadName)).Should().Equal(
            (AgendaAlertKind.SlaBreached, "Dépassé"),
            (AgendaAlertKind.SlaAtRisk, "Menacé"));
    }

    [Fact]
    public async Task Appointments_list_those_I_organise_or_am_invited_to()
    {
        var other = Guid.NewGuid();
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_me] = "Ambroise Bazié", [_colleague] = "Awa Koné" });

        Appointment Make(string title, Guid organizer, params Guid[] participants) => Appointment.Schedule(
            _tenantId, organizer, title, _now.AddDays(1), 30, "Africa/Abidjan", AppointmentMode.Online, null,
            "https://us02web.zoom.us/j/123", null, null, participants, null, _now).Value!;

        await using (var db = _db.CreateContext())
        {
            db.Appointments.AddRange(
                Make("J'organise", _me, _colleague),
                Make("On m'invite", _colleague, _me),
                Make("Sans moi", _colleague, other));
            await db.SaveChangesAsync();
        }

        var agenda = await RunAsync();

        agenda.Appointments.Select(a => a.Title).Should().BeEquivalentTo("J'organise", "On m'invite");
        var mine = agenda.Appointments.Single(a => a.Title == "J'organise");
        mine.IsOrganizer.Should().BeTrue();
        mine.MeetingProvider.Should().Be(MeetingProvider.Zoom);
        mine.Participants.Single().FullName.Should().Be("Awa Koné");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(GetMyAgendaHandler.MaxDays + 1)]
    public async Task A_window_out_of_bounds_is_refused(int days)
    {
        await using var db = _db.CreateContext();
        var handler = new GetMyAgendaHandler(db, _currentUser, _settings, _admin, _customers, TimeProvider.System);

        var result = await handler.Handle(new GetMyAgendaQuery(_now, days), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}
