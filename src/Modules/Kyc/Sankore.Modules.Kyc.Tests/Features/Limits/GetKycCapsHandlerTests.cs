namespace Sankore.Modules.Kyc.Tests.Features.Limits;

using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Kyc;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Limits.GetCaps;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The read side of the ceilings (KYC-B-06).
///
/// <para>
/// Three things are pinned here, and each one is a way the screen could lie. The amounts come from
/// the tenant parameters and never from a constant — the front's stub carried 250 000 and 500 000
/// hard-coded, which made a policy change a front release. An uncapped customer gets NO ceilings
/// rather than zeros, because a gauge drawn against a zero reads as "limit reached". And a
/// consumption nobody can measure is reported as null with a reason, never as zero, which is the
/// same discipline <c>GetFlowUsageAsync</c> imposes on its C# callers.
/// </para>
/// </summary>
public sealed class GetKycCapsHandlerTests : IDisposable
{
    private static readonly Guid MyAgency = Guid.Parse("11111111-0000-0000-0000-000000000011");
    private static readonly Guid OtherAgency = Guid.Parse("22222222-0000-0000-0000-000000000022");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly IDistributedCache _cache =
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly TimeProvider _clock = TimeProvider.System;

    public GetKycCapsHandlerTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    /// <param name="perimeter">
    /// <c>null</c> is UNRESTRICTED per IAgencyScopeProvider — a super-user, not a denial.
    /// </param>
    private GetKycCapsHandler Handler(IReadOnlySet<Guid>? perimeter = null)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);

        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.GetAccessibleAgencyIdsAsync(_tenantId, _userId, Arg.Any<CancellationToken>())
            .Returns(perimeter);

        var settings = new KycSettingsService(_factory.CreateContext(), _clock);

        var facade = new KycModuleFacade(
            _factory.CreateContext(), settings, _cache, NullLogger<KycModuleFacade>.Instance);

        return new GetKycCapsHandler(
            _factory.CreateContext(), currentUser, scope, facade, settings);
    }

    /// <summary>Drives a file to a validated tier through legal transitions only.</summary>
    private async Task<KycFile> SeedAsync(
        KycTier tier, bool expired = false, bool rejected = false, Guid? agencyId = null)
    {
        var submitter = Guid.NewGuid();
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.Agency, submitter, _clock, agencyId: agencyId ?? MyAgency);

        file.SubmitForVerification(submitter, _clock);
        file.RecordVerification(90, KycConfidenceLevel.High, _clock);

        if (rejected)
        {
            file.Reject(Guid.NewGuid(), _clock);
        }
        else
        {
            file.Approve(tier, Guid.NewGuid(), _clock);

            if (expired)
            {
                file.StartReview(_clock);
                file.Expire(_clock);
            }
        }

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    [Fact]
    public async Task A_simplified_customer_gets_the_tenant_ceilings_and_their_currency()
    {
        var file = await SeedAsync(KycTier.Simplified);

        var result = await Handler().Handle(new GetKycCapsQuery(_customerId), default);

        result.IsSuccess.Should().BeTrue();
        var caps = result.Value!;
        caps.KycFileId.Should().Be(file.Id);
        caps.CustomerId.Should().Be(_customerId);
        caps.IsCapped.Should().BeTrue();
        caps.Tier.Should().Be(nameof(KycTier.Simplified));
        caps.BalanceCap.Should().Be(250_000m);
        caps.FlowCap.Should().Be(500_000m);
        caps.FlowWindowDays.Should().Be(30, "the window is 30 ROLLING days, not a calendar month");
        caps.AlertPct.Should().Be(80);
        caps.Currency.Should().Be("XOF", "the amounts are bare decimals; the currency must travel");
    }

    [Fact]
    public async Task The_amounts_follow_the_tenant_parameter_and_not_a_constant()
    {
        // The whole point of the endpoint: the ceilings of the cahier (250 000 / 500 000) are a
        // policy, so a tenant that changes them must be obeyed without a release.
        await SeedAsync(KycTier.Simplified);

        var settings = new KycSettingsService(_factory.CreateContext(), _clock);
        await settings.SetAsync(_tenantId, KycSettingKeys.SimplifiedMaxBalance, "90000", _userId, default);
        await settings.SetAsync(_tenantId, KycSettingKeys.CapsCurrency, "GHS", _userId, default);

        var result = await Handler().Handle(new GetKycCapsQuery(_customerId), default);

        result.Value!.BalanceCap.Should().Be(90_000m);
        result.Value.Currency.Should().Be("GHS");
    }

    [Fact]
    public async Task An_uncapped_customer_gets_no_ceilings_rather_than_zeros()
    {
        // A zero ceiling is worse than no ceiling: a gauge drawn against it reads as "limit
        // reached" and the teller refuses an operation that is perfectly allowed.
        await SeedAsync(KycTier.Full);

        var caps = (await Handler().Handle(new GetKycCapsQuery(_customerId), default)).Value!;

        caps.IsCapped.Should().BeFalse();
        caps.Tier.Should().Be(nameof(KycTier.Full));
        caps.BalanceCap.Should().BeNull();
        caps.FlowCap.Should().BeNull();
        caps.FlowWindowDays.Should().BeNull();
        caps.AlertPct.Should().BeNull();
    }

    [Fact]
    public async Task An_expired_file_is_capped_and_not_cut_off()
    {
        await SeedAsync(KycTier.Full, expired: true);

        var caps = (await Handler().Handle(new GetKycCapsQuery(_customerId), default)).Value!;

        caps.IsCapped.Should().BeTrue("the relationship is valid, the review is merely overdue");
        caps.BalanceCap.Should().Be(250_000m);
    }

    [Fact]
    public async Task The_consumption_is_reported_as_unmeasured_and_never_as_zero()
    {
        // No module owns an account or a transaction. A zero would read as "nothing consumed" and
        // let an operation through on a ceiling nobody checked.
        await SeedAsync(KycTier.Simplified);

        var usage = (await Handler().Handle(new GetKycCapsQuery(_customerId), default)).Value!.Usage;

        usage.Balance.Should().BeNull();
        usage.Flow.Should().BeNull();
        usage.FlowWindowStart.Should().BeNull();
        usage.UnavailableReason.Should().Be(KycCapsUsageReasons.NoTransactionSource,
            "a screen must be able to say WHY the gauge is empty");
    }

    [Fact]
    public async Task A_customer_with_no_file_is_not_found()
    {
        var result = await Handler().Handle(new GetKycCapsQuery(Guid.NewGuid()), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_rejected_file_grants_nothing_and_answers_not_found()
    {
        await SeedAsync(KycTier.None, rejected: true);

        var result = await Handler().Handle(new GetKycCapsQuery(_customerId), default);

        result.IsFailure.Should().BeTrue(
            "a closed file is evidence, not a live position — and 'no caps' must never read as 'no limits'");
        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    [Fact]
    public async Task A_customer_of_another_agency_is_not_found_rather_than_forbidden()
    {
        // The facade bypasses every query filter by design — it answers other modules' background
        // jobs — so this handler is the only thing standing between kyc:read and another branch's
        // compliance position.
        await SeedAsync(KycTier.Simplified, agencyId: OtherAgency);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycCapsQuery(_customerId), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound,
            "a 403 would confirm the customer has a file here, which is what the perimeter hides");
    }

    [Fact]
    public async Task A_customer_of_my_agency_is_readable_within_a_restricted_perimeter()
    {
        await SeedAsync(KycTier.Simplified, agencyId: MyAgency);

        var result = await Handler(new HashSet<Guid> { MyAgency })
            .Handle(new GetKycCapsQuery(_customerId), default);

        result.IsSuccess.Should().BeTrue();
    }
}
