namespace Sankore.Modules.Kyc.Tests.Features.Reviews;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Who hears that an identity document is about to expire, or has (2026-11-05).</summary>
public sealed class KycAlertsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 1);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly FixedClock _clock = new(Now);
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public KycAlertsTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private Task<int> RunAsync() => KycAlerts.SendExpiryAlertsAsync(
        _db, _notifications, _administration, _customers, _tenantId, Today, CancellationToken.None);

    [Fact]
    public async Task A_piece_expiring_within_the_window_tells_the_responsible_agent()
    {
        var file = await SeedFileAsync();
        await SeedDocumentAsync(file, Today.AddDays(10));

        (await RunAsync()).Should().Be(1);

        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n =>
                n.UserId == _agent && n.Title == "Pièce d'identité bientôt expirée" && n.Link == $"/kyc/{file.Id}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_piece_says_so()
    {
        var file = await SeedFileAsync();
        await SeedDocumentAsync(file, Today.AddDays(-3));

        await RunAsync();

        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.Title == "Pièce d'identité expirée"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_piece_retaken_since_has_nothing_left_to_say()
    {
        var file = await SeedFileAsync();
        await SeedDocumentAsync(file, Today.AddDays(5));
        await SeedDocumentAsync(file, Today.AddYears(5));

        (await RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_piece_far_from_expiry_is_silent()
    {
        var file = await SeedFileAsync();
        await SeedDocumentAsync(file, Today.AddDays(KycAlerts.ExpiryWindowDays + 1));

        (await RunAsync()).Should().Be(0);
    }

    private async Task<KycFile> SeedFileAsync()
    {
        var file = KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, _agent, _clock);
        file.SubmitForVerification(_agent, _clock);
        file.RecordVerification(90, KycConfidenceLevel.High, _clock);
        file.Approve(KycTier.Full, Guid.NewGuid(), _clock);
        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private async Task SeedDocumentAsync(KycFile file, DateOnly expiry)
    {
        _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            file.TenantId, file.Id, "NationalIdCard",
            encryptedNumber: "v1:nonce:tag:ciphertext",
            numberBlindIndex: Guid.NewGuid().ToString("N"),
            clock: _clock,
            expiryDate: expiry));
        await _db.SaveChangesAsync();
    }
}
