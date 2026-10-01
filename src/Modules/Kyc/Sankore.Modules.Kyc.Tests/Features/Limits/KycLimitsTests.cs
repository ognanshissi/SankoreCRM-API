namespace Sankore.Modules.Kyc.Tests.Features.Limits;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Limits.Consumers;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Xunit;

/// <summary>
/// The ceilings M03 and M07 will enforce. Those modules do not exist, so what is testable today is
/// the contract itself: which tier a file grants, that a closed file grants nothing, that the
/// cache cannot outlive a downgrade, and that an unmeasurable flow is reported as unmeasurable
/// rather than as zero.
/// </summary>
public sealed class KycLimitsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    public KycLimitsTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private KycModuleFacade Facade() => new(
        _factory.CreateContext(),
        new KycSettingsService(_factory.CreateContext(), TimeProvider.System),
        _cache,
        NullLogger<KycModuleFacade>.Instance);

    /// <summary>Drives a file to a validated tier through legal transitions only.</summary>
    private async Task<KycFile> SeedAsync(KycTier tier, bool expired = false, bool closed = false)
    {
        var submitter = Guid.NewGuid();
        var file = KycFile.Open(_tenantId, _customerId, KycChannel.Agency, submitter, TimeProvider.System);

        if (closed)
        {
            file.SubmitForVerification(submitter, TimeProvider.System);
            file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System);
            file.Reject(Guid.NewGuid(), TimeProvider.System);
        }
        else
        {
            file.SubmitForVerification(submitter, TimeProvider.System);
            file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System);
            file.Approve(tier, Guid.NewGuid(), TimeProvider.System);

            if (expired)
            {
                file.StartReview(TimeProvider.System);
                file.Expire(TimeProvider.System);
            }
        }

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    [Fact]
    public async Task A_customer_with_no_file_gets_null_and_not_an_absence_of_limits()
    {
        // The difference matters: null means "may not operate", not "operate freely".
        (await Facade().GetLimitsAsync(_tenantId, Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_simplified_file_is_capped_with_the_tenant_ceilings()
    {
        await SeedAsync(KycTier.Simplified);

        var limits = await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);

        limits!.IsCapped.Should().BeTrue();
        limits.Tier.Should().Be("Simplified");
        limits.MaxBalance.Should().Be(250_000m);
        limits.MaxFlow.Should().Be(500_000m);
        limits.WindowDays.Should().Be(30);
        limits.AlertPct.Should().Be(80);
    }

    [Fact]
    public async Task A_full_file_is_not_capped()
    {
        await SeedAsync(KycTier.Full);

        var limits = await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);

        limits!.IsCapped.Should().BeFalse();
        limits.Tier.Should().Be("Full");
    }

    [Fact]
    public async Task An_expired_full_file_falls_back_to_the_simplified_ceilings()
    {
        // Capped, not cut off: the relationship is valid, the review is merely overdue.
        await SeedAsync(KycTier.Full, expired: true);

        var limits = await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);

        limits!.IsCapped.Should().BeTrue();
        limits.MaxBalance.Should().Be(250_000m);
    }

    [Fact]
    public async Task A_rejected_file_grants_nothing()
    {
        await SeedAsync(KycTier.None, closed: true);

        (await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_ceiling_overrides_the_default()
    {
        await SeedAsync(KycTier.Simplified);
        await new KycSettingsService(_factory.CreateContext(), TimeProvider.System).SetAsync(
            _tenantId, KycSettingKeys.SimplifiedMaxBalance, "90000",
            Guid.NewGuid(), CancellationToken.None);

        var limits = await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);

        limits!.MaxBalance.Should().Be(90_000m);
    }

    // ── the cache ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tier_change_drops_the_cache_at_once()
    {
        // Without this the five-minute TTL would let a customer who has just been capped keep
        // operating against the uncapped ceilings — every operation accepted in that window is
        // one a regulator would ask about.
        await SeedAsync(KycTier.Full);
        var before = await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);
        before!.IsCapped.Should().BeFalse();

        var context = Substitute.For<ConsumeContext<KycTierChangedEvent>>();
        context.Message.Returns(new KycTierChangedEvent(
            _tenantId, _customerId, "Full", "Simplified", DateTimeOffset.UtcNow));
        context.CancellationToken.Returns(CancellationToken.None);

        await new KycTierChangedCacheConsumer(
            _cache, NullLogger<KycTierChangedCacheConsumer>.Instance).Consume(context);

        var generation = await KycModuleFacade.ReadLimitsGenerationAsync(
            _cache, _tenantId, CancellationToken.None);

        _cache.GetString(KycModuleFacade.LimitsCacheKey(_tenantId, _customerId, generation))
            .Should().BeNull("the next read must go back to the file");
    }

    [Fact]
    public async Task The_cache_never_serves_another_customer()
    {
        await SeedAsync(KycTier.Simplified);
        await Facade().GetLimitsAsync(_tenantId, _customerId, CancellationToken.None);

        (await Facade().GetLimitsAsync(_tenantId, Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    // ── the flow ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_flow_is_reported_as_unmeasurable_and_never_as_zero()
    {
        // No module owns an account or a transaction. Answering zero would read as "nothing
        // consumed" and let an operation through on a ceiling nobody checked.
        var usage = await Facade().GetFlowUsageAsync(_tenantId, _customerId, CancellationToken.None);

        usage.Known.Should().BeFalse();
        usage.Should().Be(KycFlowUsage.Unknown);
    }
}
