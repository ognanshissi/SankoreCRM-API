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
}
