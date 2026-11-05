namespace Sankore.Modules.Leads.Tests.Features.Tasks;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.WorkAlerts;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>The bell for what comes due: a late task, an appointment about to start, a reminder (2026-11-05).</summary>
public sealed class WorkAlertsTests : IDisposable
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly List<InAppNotificationRequest> _sent = [];

    public WorkAlertsTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(
            ci => { _sent.Add(ci.Arg<InAppNotificationRequest>()); return Result.Ok(Guid.NewGuid()); });
        _notifications.FindInAppKeysAsync(default, default!, default!, default).ReturnsForAnyArgs(
            _ => (IReadOnlySet<string>)_sent.Select(s => s.IdempotencyKey).ToHashSet());
        _admin.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(
            ci => ((IReadOnlyCollection<Guid>)ci[1]).ToDictionary(id => id, _ => "Awa Konan") as IReadOnlyDictionary<Guid, string>);
        _admin.GetUserSupervisorsAsync(_tenantId, _agent, Arg.Any<CancellationToken>())
            .Returns(new UserSupervisors(_manager, null));
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> RunAsync()
    {
        await using var db = _db.CreateContext();
        return await WorkAlertsJob.RunAsync(_tenantId, db, _notifications, _admin, TimeProvider.System, NullLogger.Instance, default);
    }

    [Fact]
    public async Task A_late_task_tells_its_holder_once()
    {
        await SeedTaskAsync(_now.AddHours(-2));

        (await RunAsync()).Should().Be(1);
        (await RunAsync()).Should().Be(0, "the next run has nothing new to say");

        _sent.Should().ContainSingle().Which.UserId.Should().Be(_agent);
    }

    [Fact]
    public async Task A_day_later_its_manager_hears_too()
    {
        await SeedTaskAsync(_now.AddHours(-25));

        await RunAsync();

        _sent.Select(s => s.UserId).Should().BeEquivalentTo([_agent, _manager]);
        _sent.Single(s => s.UserId == _manager).Body.Should().Contain("Awa Konan");
    }

    [Fact]
    public async Task A_task_late_for_long_does_not_ring_at_the_first_run()
    {
        await SeedTaskAsync(_now.AddDays(-10));

        (await RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_finished_task_is_silent()
    {
        var task = CrmTask.Create(_tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, "Faite",
            _now.AddHours(-2), assignedAgentId: _agent);
        task.Complete();
        await using (var db = _db.CreateContext())
        {
            db.CrmTasks.Add(task);
            await db.SaveChangesAsync();
        }

        (await RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_appointment_about_to_start_tells_its_organiser_and_guests()
    {
        var guest = Guid.NewGuid();
        await SeedAppointmentAsync(_now.AddMinutes(20), guest);
        await SeedAppointmentAsync(_now.AddHours(3), Guid.NewGuid());

        await RunAsync();

        _sent.Select(s => s.UserId).Should().BeEquivalentTo([_agent, guest]);
        _sent.Should().OnlyContain(s => s.Category == WorkAlertsJob.AppointmentCategory);
    }

    private async Task SeedTaskAsync(DateTimeOffset dueAt)
    {
        await using var db = _db.CreateContext();
        db.CrmTasks.Add(CrmTask.Create(_tenantId, CrmTaskType.FirstContact, CrmTaskPriority.High, "Appeler",
            dueAt, assignedAgentId: _agent));
        await db.SaveChangesAsync();
    }

    private async Task SeedAppointmentAsync(DateTimeOffset startAt, Guid guest)
    {
        await using var db = _db.CreateContext();
        db.Appointments.Add(Appointment.Schedule(_tenantId, _agent, "Point client", startAt, 30, null,
            AppointmentMode.InPerson, null, null, null, null, [guest], null, _now).Value!);
        await db.SaveChangesAsync();
    }
}
