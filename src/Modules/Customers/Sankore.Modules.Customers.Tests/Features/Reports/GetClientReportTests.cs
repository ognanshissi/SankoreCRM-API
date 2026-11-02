namespace Sankore.Modules.Customers.Tests.Features.Reports;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Reports.GetClientReport;
using Sankore.Modules.Customers.Tests.Features.Lifecycle;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The « Clients » tab of the reports (reports lot 3, 2026-11-02): which clients each figure counts,
/// the period before, and the agency filter.
/// </summary>
public sealed class GetClientReportTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherAgency = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset From = Now.AddDays(-14);
    private static readonly DateTimeOffset To = Now.AddMinutes(1);

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private static T CreatedAt<T>(T entity, DateTimeOffset at) where T : class
    {
        typeof(T).GetProperty("CreatedAt")!.SetValue(entity, at);
        return entity;
    }

    private async Task SaveAsync(params object[] entities)
    {
        await using var db = _factory.CreateContext();
        foreach (var e in entities) db.Add(e);
        await db.SaveChangesAsync();
    }

    private async Task<ClientReportDto> RunAsync(Guid? agencyId = null)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.CurrentTenantId.Returns(TenantId);
        var handler = new GetClientReportHandler(
            _factory.CreateContext(), tenant, Substitute.For<IAdministrationModule>(), new Clock(Now));
        var result = await handler.Handle(new GetClientReportQuery(From, To, agencyId), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    [Fact]
    public async Task New_clients_count_in_their_period_and_the_base_is_read_today()
    {
        await SaveAsync(
            CreatedAt(ClientBuilder.Active(TenantId, AgencyId, Actor, advisorUserId: Actor), Now.AddDays(-2)),
            CreatedAt(ClientBuilder.Active(TenantId, AgencyId, Actor), Now.AddDays(-20)),
            CreatedAt(ClientBuilder.PendingKyc(TenantId, AgencyId, Actor), Now.AddDays(-60)),
            CreatedAt(ClientBuilder.Archived(TenantId, AgencyId, Actor), Now.AddDays(-3)));

        var report = await RunAsync();

        report.Kpis.Period.NewClients.Should().Be(2, "the archived client was created in the period too");
        report.Previous.NewClients.Should().Be(1);
        report.Kpis.Active.Should().Be(2);
        report.Kpis.WithoutAdvisor.Should().Be(2, "the second active client and the one awaiting KYC");
        report.ByType.Single(t => t.Type == ClientType.Individual).Base.Should().Be(3);
        report.Kpis.Period.SuspendedOrArchived.Should().Be(1);
    }

    [Fact]
    public async Task Complaints_give_their_handling_time_and_the_overdue_ones()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, Actor);
        var answered = Complaint(client, Now.AddDays(-6));
        answered.Resolve(Actor, "enc:Remboursé", Now.AddDays(-2)).IsSuccess.Should().BeTrue();
        var late = Complaint(client, Now.AddDays(-40));
        await SaveAsync(client, answered, late);

        var report = await RunAsync();

        report.Kpis.Period.ComplaintsReceived.Should().Be(1);
        report.Kpis.Period.ComplaintsResolved.Should().Be(1);
        report.Kpis.Period.AvgResolutionDays.Should().Be(4);
        report.Kpis.OpenComplaints.Should().Be(1);
        report.Kpis.OverdueComplaints.Should().Be(1);
    }

    [Fact]
    public async Task The_agency_filter_keeps_only_that_agency_s_clients()
    {
        await SaveAsync(
            ClientBuilder.Active(TenantId, AgencyId, Actor),
            ClientBuilder.Active(TenantId, OtherAgency, Actor));

        (await RunAsync(AgencyId)).Kpis.Active.Should().Be(1);
        (await RunAsync()).Kpis.Active.Should().Be(2);
    }

    private static ClientComplaint Complaint(Client client, DateTimeOffset receivedAt)
        => ClientComplaint.Record(
            TenantId, $"RCL-2026-{Random.Shared.Next(100000, 999999)}", client.Id, client.AgencyId, null,
            default, default, "Frais prélevés", null, Actor, null, null, receivedAt,
            acknowledgementDays: 2, resolutionDays: 15);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
