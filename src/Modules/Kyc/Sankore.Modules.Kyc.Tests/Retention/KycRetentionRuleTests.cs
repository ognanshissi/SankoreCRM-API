namespace Sankore.Modules.Kyc.Tests.Retention;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The KYC half of « may this client be erased » (data-purge lot 2). Before, it answered no
/// unconditionally, so no client could ever be anonymised.
/// </summary>
public sealed class KycRetentionRuleTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public KycRetentionRuleTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private KycModuleFacade Facade() => new(
        _factory.CreateContext(), new KycSettingsService(_factory.CreateContext(), TimeProvider.System),
        Substitute.For<IDistributedCache>(), NullLogger<KycModuleFacade>.Instance);

    [Fact]
    public async Task A_client_without_any_KYC_file_is_released()
        => (await Facade().IsRetentionClearedAsync(_tenantId, _customerId, default)).Should().BeTrue();

    [Fact]
    public async Task A_file_still_being_collected_holds_the_evidence()
    {
        await using (var db = _factory.CreateContext())
        {
            db.KycFiles.Add(KycFile.Open(_tenantId, _customerId, KycChannel.Agency, Guid.NewGuid(), TimeProvider.System));
            await db.SaveChangesAsync();
        }

        (await Facade().IsRetentionClearedAsync(_tenantId, _customerId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_clients_open_file_does_not_hold_this_one()
    {
        await using (var db = _factory.CreateContext())
        {
            db.KycFiles.Add(KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), TimeProvider.System));
            await db.SaveChangesAsync();
        }

        (await Facade().IsRetentionClearedAsync(_tenantId, _customerId, default)).Should().BeTrue();
    }
}
