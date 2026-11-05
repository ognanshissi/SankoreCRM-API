namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.PolicySync;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Who hears that an insurance contract is about to end (2026-11-05).</summary>
public sealed class PolicyExpiryAlertsTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _advisor = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();

    public PolicyExpiryAlertsTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenantId);
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
        Client(_advisor);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_contract_ending_within_the_window_tells_the_advisor()
    {
        await SeedAsync(PolicyStatus.Issued, Today.AddDays(20));

        (await RunAsync()).Should().Be(1);

        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.UserId == _advisor && n.Body!.Contains("Awa Konan")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_an_advisor_the_agency_hears_it()
    {
        var head = Guid.NewGuid();
        Client(null);
        _administration.ResolveAgencyRecipientAsync(_tenantId, _agency, null, Arg.Any<CancellationToken>()).Returns(head);
        await SeedAsync(PolicyStatus.Issued, Today.AddDays(20));

        await RunAsync();

        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.UserId == head), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_contract_far_from_its_end_or_no_longer_live_is_silent()
    {
        await SeedAsync(PolicyStatus.Issued, Today.AddDays(PolicyExpiryAlertsJob.WindowDays + 1));
        await SeedAsync(PolicyStatus.Cancelled, Today.AddDays(5));
        await SeedAsync(PolicyStatus.Issued, Today.AddDays(-1));

        (await RunAsync()).Should().Be(0);
    }

    private async Task<int> RunAsync()
    {
        await using var db = _factory.CreateContext();
        return await PolicyExpiryAlertsJob.SendAsync(_tenantId, Today, db, _customers, _administration, _notifications, default);
    }

    private void Client(Guid? advisor) =>
        _customers.GetClientSummariesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ClientSummary>
            {
                [_client] = new(_client, "C-1", "Individual", "Awa Konan", "Active", _agency, advisor, "Approved", "Low", null),
            });

    private async Task SeedAsync(PolicyStatus status, DateOnly expiry)
    {
        await using var db = _factory.CreateContext();
        db.Policies.Add(PolicyRecord.Create(
            _tenantId, Guid.NewGuid(), _client, Guid.NewGuid().ToString(), $"POL-{Guid.NewGuid():N}", "OBS",
            status, Today.AddYears(-1), 10_000, "XOF", PremiumPeriodicity.Monthly, TimeProvider.System, expiryDate: expiry));
        await db.SaveChangesAsync();
    }
}
