namespace Sankore.Modules.Leads.Tests.Features.Appointments;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Appointments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class AppointmentTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public AppointmentTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Roles.Returns(["Agent"]);
        _admin.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_colleague] = "Awa Koné" });
    }

    public void Dispose() => _db.Dispose();

    private Result<Appointment> Schedule(AppointmentMode mode = AppointmentMode.Online, string? url = "https://teams.microsoft.com/l/meetup-join/abc",
        DateTimeOffset? startAt = null, Guid? leadId = null, Guid? clientId = null)
        => Appointment.Schedule(_tenantId, _me, "Point dossier", startAt ?? _now.AddHours(2), 45, "Africa/Abidjan",
            mode, "Agence Plateau", url, leadId, clientId, [_colleague, _me, _colleague], null, _now);

    // ——— Domaine ———

    [Theory]
    [InlineData("https://us02web.zoom.us/j/8123", MeetingProvider.Zoom)]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc", MeetingProvider.Teams)]
    [InlineData("https://meet.google.com/abc-defg-hij", MeetingProvider.GoogleMeet)]
    [InlineData("https://visio.example.org/salle-1", MeetingProvider.Other)]
    public void The_provider_is_read_from_the_link(string url, MeetingProvider provider)
        => Schedule(url: url).Value!.MeetingProvider.Should().Be(provider);

    [Theory]
    [InlineData(null)]
    [InlineData("zoom.us/j/123")]
    [InlineData("javascript:alert(1)")]
    public void An_online_meeting_needs_a_web_link(string? url)
        => Schedule(url: url).Error.Should().Be("APPOINTMENT_MEETING_URL_INVALID");

    [Fact]
    public void An_in_person_meeting_keeps_its_place_and_drops_any_link()
    {
        var appointment = Schedule(AppointmentMode.InPerson).Value!;
        appointment.Location.Should().Be("Agence Plateau");
        appointment.MeetingUrl.Should().BeNull();
    }

    [Fact]
    public void The_organiser_is_not_their_own_participant_and_duplicates_collapse()
        => Schedule().Value!.ParticipantIds.Should().Equal(_colleague);

    [Fact]
    public void A_meeting_in_the_past_is_refused()
        => Schedule(startAt: _now.AddMinutes(-5)).Error.Should().Be("APPOINTMENT_IN_PAST");

    [Fact]
    public void A_meeting_is_about_a_lead_or_a_client_not_both()
        => Schedule(leadId: Guid.NewGuid(), clientId: Guid.NewGuid()).Error.Should().Be("APPOINTMENT_ONE_SUBJECT_ONLY");

    // ——— Planifier ———

    private ScheduleAppointmentRequest Request(IReadOnlyList<Guid>? participants = null, Guid? clientId = null) => new(
        "Point dossier", _now.AddDays(1), 30, "Africa/Abidjan", AppointmentMode.Online, null,
        "https://us02web.zoom.us/j/8123", null, clientId, participants ?? [_colleague], "Ordre du jour");

    private async Task<Result<Guid>> ScheduleAsync(ScheduleAppointmentRequest request)
    {
        await using var db = _db.CreateContext();
        return await new ScheduleAppointmentHandler(db, _currentUser, _admin, _customers, _publisher, TimeProvider.System)
            .Handle(new ScheduleAppointmentCommand(request), CancellationToken.None);
    }

    [Fact]
    public async Task Scheduling_saves_the_appointment_and_queues_the_invitations()
    {
        var result = await ScheduleAsync(Request());

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var db = _db.CreateContext();
        var saved = await db.Appointments.SingleAsync();
        saved.OrganizerId.Should().Be(_me);
        await _publisher.Received(1).PublishAsync(
            Arg.Is<AppointmentScheduledEvent>(e => e.AppointmentId == saved.Id && e.ParticipantIds.SequenceEqual(new[] { _colleague })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_participant_from_outside_the_tenant_is_refused()
        => (await ScheduleAsync(Request([Guid.NewGuid()]))).Error.Should().Be("APPOINTMENT_PARTICIPANT_UNKNOWN");

    [Fact]
    public async Task A_client_outside_my_perimeter_reads_as_absent()
    {
        var clientId = Guid.NewGuid();
        _customers.ResolveClientIdAsync(_tenantId, clientId, Arg.Any<CancellationToken>()).Returns(clientId);
        _customers.CanActorAccessClientAsync(_tenantId, clientId, _me, Arg.Any<CancellationToken>()).Returns(false);

        (await ScheduleAsync(Request(clientId: clientId))).Error.Should().Be("CLIENT_NOT_FOUND");
    }

    // ——— Annuler ———

    [Fact]
    public async Task Only_the_organiser_or_a_supervisor_cancels()
    {
        var appointment = Appointment.Schedule(_tenantId, _colleague, "Réunion d'agence", _now.AddDays(1), 60, null,
            AppointmentMode.InPerson, null, null, null, null, [_me], null, _now).Value!;
        await using (var db = _db.CreateContext())
        {
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
        }

        async Task<Result> CancelAsync()
        {
            await using var db = _db.CreateContext();
            return await new CancelAppointmentHandler(db, _currentUser, _publisher, TimeProvider.System)
                .Handle(new CancelAppointmentCommand(appointment.Id, "Report"), CancellationToken.None);
        }

        (await CancelAsync()).Error.Should().Be("NOT_APPOINTMENT_ORGANIZER");

        _currentUser.Roles.Returns(["BranchManager"]);
        (await CancelAsync()).IsSuccess.Should().BeTrue();
        await _publisher.Received(1).PublishAsync(Arg.Any<AppointmentCancelledEvent>(), Arg.Any<CancellationToken>());
    }

    // ——— Déplacer ———

    [Fact]
    public void Moving_keeps_the_duration_unless_a_new_one_is_given()
    {
        var appointment = Schedule(AppointmentMode.InPerson, null).Value!;
        var later = _now.AddDays(2);

        appointment.Reschedule(later, null, _now).IsSuccess.Should().BeTrue();
        appointment.StartAt.Should().Be(later);
        appointment.DurationMinutes.Should().Be(45);

        appointment.Reschedule(later, 30, _now).IsSuccess.Should().BeTrue();
        appointment.DurationMinutes.Should().Be(30);
    }

    [Fact]
    public void Moving_to_the_past_or_moving_a_cancelled_one_is_refused()
    {
        var appointment = Schedule(AppointmentMode.InPerson, null).Value!;
        appointment.Reschedule(_now.AddMinutes(-5), null, _now).Error.Should().Be("APPOINTMENT_IN_PAST");
        appointment.Reschedule(_now.AddDays(1), 2, _now).Error.Should().Be("APPOINTMENT_DURATION_INVALID");

        appointment.Cancel(null, _now);
        appointment.Reschedule(_now.AddDays(1), null, _now).Error.Should().Be("APPOINTMENT_ALREADY_CANCELLED");
    }

    [Fact]
    public async Task Moving_mails_the_participants_once_and_a_move_to_the_same_slot_mails_nobody()
    {
        _currentUser.Id.Returns(_me);
        var appointment = Appointment.Schedule(_tenantId, _me, "Point client", _now.AddDays(1), 60, null,
            AppointmentMode.InPerson, null, null, null, null, [_colleague], null, _now).Value!;
        await using (var db = _db.CreateContext())
        {
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
        }

        async Task<Result> MoveAsync(DateTimeOffset startAt)
        {
            await using var db = _db.CreateContext();
            return await new RescheduleAppointmentHandler(db, _currentUser, _publisher, TimeProvider.System)
                .Handle(new RescheduleAppointmentCommand(appointment.Id, startAt, null), CancellationToken.None);
        }

        var target = appointment.StartAt.AddHours(2);
        (await MoveAsync(target)).IsSuccess.Should().BeTrue();
        (await MoveAsync(target)).IsSuccess.Should().BeTrue();

        await _publisher.Received(1).PublishAsync(
            Arg.Is<AppointmentRescheduledEvent>(e => e.StartAt == target && e.ParticipantIds.Contains(_colleague)),
            Arg.Any<CancellationToken>());
        await using var check = _db.CreateContext();
        (await check.Appointments.SingleAsync(a => a.Id == appointment.Id)).StartAt.Should().Be(target);
    }

    [Fact]
    public async Task Only_the_organiser_or_a_supervisor_moves()
    {
        var appointment = Appointment.Schedule(_tenantId, _colleague, "Réunion d'agence", _now.AddDays(1), 60, null,
            AppointmentMode.InPerson, null, null, null, null, [_me], null, _now).Value!;
        await using (var db = _db.CreateContext())
        {
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        var result = await new RescheduleAppointmentHandler(ctx, _currentUser, _publisher, TimeProvider.System)
            .Handle(new RescheduleAppointmentCommand(appointment.Id, _now.AddDays(2), null), CancellationToken.None);
        result.Error.Should().Be("NOT_APPOINTMENT_ORGANIZER");
    }

    // ——— Pour les agendas connectés ———

    [Fact]
    public async Task A_user_reads_the_live_appointments_they_organise_or_attend_in_a_window()
    {
        Appointment Plan(Guid organizer, Guid[] participants, int days) =>
            Appointment.Schedule(_tenantId, organizer, $"J+{days}", _now.AddDays(days), 60, null,
                AppointmentMode.InPerson, null, null, null, null, participants, null, _now).Value!;

        var mine = Plan(_me, [], 1);
        var invited = Plan(_colleague, [_me], 2);
        var cancelled = Plan(_me, [], 3);
        cancelled.Cancel(null, _now);
        var notMine = Plan(_colleague, [], 1);
        var tooFar = Plan(_me, [], 40);

        await using (var db = _db.CreateContext())
        {
            db.Appointments.AddRange(mine, invited, cancelled, notMine, tooFar);
            await db.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var facade = TestLeadsFacade.Build(read, tenantId: _tenantId);

        var listed = await facade.ListAppointmentsForUserAsync(_tenantId, _me, _now, _now.AddDays(30), CancellationToken.None);
        listed.Select(a => a.Id).Should().Equal(mine.Id, invited.Id);

        var entry = await facade.GetAppointmentForCalendarAsync(_tenantId, cancelled.Id, CancellationToken.None);
        entry!.IsCancelled.Should().BeTrue();
        (await facade.GetAppointmentForCalendarAsync(Guid.NewGuid(), mine.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Every_change_is_announced_for_the_agenda_copies_even_without_participants()
    {
        var result = await ScheduleAsync(Request(participants: []));

        result.IsSuccess.Should().BeTrue();
        await _publisher.Received(1).PublishAsync(
            Arg.Is<Sankore.Modules.Leads.PublicApi.AppointmentChangedIntegrationEvent>(e => e.AppointmentId == result.Value),
            Arg.Any<CancellationToken>());
        await _publisher.DidNotReceive().PublishAsync(Arg.Any<AppointmentScheduledEvent>(), Arg.Any<CancellationToken>());
    }
}
