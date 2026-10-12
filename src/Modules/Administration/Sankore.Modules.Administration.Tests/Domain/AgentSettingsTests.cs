namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class AgentSettingsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();

    private static AgentSettings Fresh() => AgentSettings.Create(Tenant, Agent);

    private static List<WorkingDay> Week(string start = "08:00", string end = "17:00") =>
        Enum.GetValues<DayOfWeek>()
            .Select(d => new WorkingDay(d, d != DayOfWeek.Sunday, start, end))
            .ToList();

    private static bool Update(AgentSettings s, AgentAvailability status, AbsenceDeclaration? absence = null,
        List<WorkingDay>? hours = null, int? capacity = null)
        => s.UpdateBySelf(status, absence, hours ?? [], capacity, []);

    // ── Statut ────────────────────────────────────────────────────────────

    [Fact]
    public void An_agent_without_settings_constraints_is_dispatchable()
        => Fresh().IsDispatchable(DateTimeOffset.UtcNow, activeLeads: 0).Should().BeTrue();

    [Fact]
    public void A_paused_agent_receives_no_new_lead()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Paused);
        s.IsDispatchable(DateTimeOffset.UtcNow, 0).Should().BeFalse();
    }

    [Fact]
    public void An_absence_only_excludes_the_agent_during_its_period()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Absent, new AbsenceDeclaration(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), null, false));

        s.AllowsDispatchOn(new DateOnly(2026, 10, 18)).Should().BeTrue("l'absence n'a pas commencé");
        s.AllowsDispatchOn(new DateOnly(2026, 10, 19)).Should().BeFalse();
        s.AllowsDispatchOn(new DateOnly(2026, 10, 23)).Should().BeFalse("la fin est incluse");
        s.AllowsDispatchOn(new DateOnly(2026, 10, 24)).Should().BeTrue("l'absence est passée");
    }

    [Fact]
    public void Absent_requires_dates()
    {
        var act = () => Update(Fresh(), AgentAvailability.Absent);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Transferring_tasks_requires_a_replacement()
    {
        var act = () => Update(Fresh(), AgentAvailability.Absent,
            new AbsenceDeclaration(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), null, TransferTasks: true));
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Only_a_new_or_changed_absence_is_reported()
    {
        var s = Fresh();
        var absence = new AbsenceDeclaration(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), Guid.NewGuid(), true);

        Update(s, AgentAvailability.Absent, absence).Should().BeTrue();
        Update(s, AgentAvailability.Absent, absence).Should().BeFalse("rien n'a changé : pas de second e-mail ni de second transfert");
        Update(s, AgentAvailability.Absent, absence with { To = new DateOnly(2026, 10, 24) }).Should().BeTrue();
    }

    [Fact]
    public void Leaving_absent_clears_the_absence()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Absent, new AbsenceDeclaration(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23), Guid.NewGuid(), true));
        Update(s, AgentAvailability.Available);

        s.CurrentAbsence.Should().BeNull();
        s.ReplacementUserId.Should().BeNull();
        s.TransferTasksToReplacement.Should().BeFalse();
    }

    // ── Horaires ──────────────────────────────────────────────────────────

    [Fact]
    public void Outside_working_hours_the_agent_is_not_dispatchable()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Available, hours: Week());

        var mondayMorning = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
        var mondayEvening = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);

        s.IsWithinWorkingHours(mondayMorning).Should().BeTrue();
        s.IsWithinWorkingHours(mondayEvening).Should().BeFalse();
        s.IsWithinWorkingHours(sunday).Should().BeFalse();
    }

    [Fact]
    public void Hours_must_cover_the_whole_week_in_order()
    {
        var incomplete = () => Update(Fresh(), AgentAvailability.Available, hours: Week().Take(5).ToList());
        incomplete.Should().Throw<DomainException>();

        var reversed = () => Update(Fresh(), AgentAvailability.Available, hours: Week("17:00", "08:00"));
        reversed.Should().Throw<DomainException>();
    }

    // ── Capacité ──────────────────────────────────────────────────────────

    [Fact]
    public void Capacity_cannot_exceed_the_ceiling()
    {
        var act = () => Update(Fresh(), AgentAvailability.Available, capacity: AgentSettings.DefaultCapacityCeiling + 1);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_full_agent_is_not_dispatchable()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Available, capacity: 25);

        s.IsDispatchable(DateTimeOffset.UtcNow, activeLeads: 24).Should().BeTrue();
        s.IsDispatchable(DateTimeOffset.UtcNow, activeLeads: 25).Should().BeFalse();
    }

    [Fact]
    public void Lowering_the_ceiling_brings_the_capacity_down_with_it()
    {
        var s = Fresh();
        Update(s, AgentAvailability.Available, capacity: 25);

        s.SetCapacityCeiling(20);

        s.MaxActiveLeads.Should().Be(20);
        s.EffectiveCapacity.Should().Be(20);
    }
}
