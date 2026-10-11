namespace Sankore.Modules.Kyc.Tests.Features.Reviews;

using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// « Les informations sont mises à jour à la date anniversaire du contrat » — a rule with its own
/// clock, which the periodic review could not express.
///
/// <para>
/// The periodic sweep runs on the vigilance level: three years for a low-risk file, one for a
/// high-risk one. A client holding a contract signed in March is to be re-examined every March
/// whatever their rating says, and nothing made that happen.
/// </para>
/// </summary>
public sealed class ContractAnniversaryReviewJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 2, 15, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public ContractAnniversaryReviewJobTests()
    {
        _factory = new TestKycDbContextFactory(_tenant);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private void AnniversariesToday(params Guid[] clientIds)
        => _customers.GetClientsWithContractAnniversaryAsync(
                _tenant, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(clientIds.ToList());

    private async Task<KycFile> SeedFileAsync(
        Guid? customerId = null, KycFileStatus status = KycFileStatus.Full)
    {
        var file = KycFile.Open(
            _tenant, customerId ?? _clientId, KycChannel.Agency, Guid.NewGuid(),
            new FixedClock(Now));

        if (status != KycFileStatus.Collecting)
        {
            typeof(KycFile).GetProperty(nameof(KycFile.Status))!.SetValue(file, status);
        }

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private async Task RunAsync()
    {
        var services = new ServiceCollection();

        services.AddSingleton(_db);
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddSingleton(_customers);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddKeyedSingleton(nameof(KycDbContext), Substitute.For<IEventPublisher>());
        services.AddSingleton(Substitute.For<IBackgroundJobClient>());

        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);
        services.AddSingleton(settings);

        services.AddScoped<KycReviewScheduler>();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(KycModule).Assembly));

        await using var provider = services.BuildServiceProvider();

        await new ContractAnniversaryReviewJob(
                new FixedScopeFactory(provider),
                NullLogger<ContractAnniversaryReviewJob>.Instance)
            .RunAsync(_tenant, CancellationToken.None);
    }

    private Task<List<KycReviewSchedule>> Reviews()
        => _db.KycReviewSchedules.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task An_anniversary_today_raises_a_review_on_the_clients_open_file()
    {
        var file = await SeedFileAsync();
        AnniversariesToday(_clientId);

        await RunAsync();

        var review = (await Reviews()).Should().ContainSingle().Subject;
        review.KycFileId.Should().Be(file.Id);
        review.Trigger.Should().Be(KycReviewTrigger.Event);
        review.Reason.Should().Be(ContractAnniversaryReviewJob.AnniversaryReason);
        review.DueDate.Should().Be(Today);
    }

    [Fact]
    public async Task No_anniversary_today_raises_nothing()
    {
        await SeedFileAsync();
        AnniversariesToday();

        await RunAsync();

        (await Reviews()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_with_no_open_file_is_skipped_rather_than_failing_the_run()
    {
        // And the rest of the tenant's anniversaries are still owed today, which is why the loop
        // continues instead of throwing.
        var withFile = Guid.NewGuid();
        await SeedFileAsync(customerId: withFile);

        AnniversariesToday(Guid.NewGuid(), withFile);

        await RunAsync();

        (await Reviews()).Should().HaveCount(1);
    }

    [Theory]
    [InlineData(KycFileStatus.Rejected)]
    [InlineData(KycFileStatus.Suspended)]
    public async Task A_closed_file_is_not_reviewed(KycFileStatus status)
    {
        await SeedFileAsync(status: status);
        AnniversariesToday(_clientId);

        await RunAsync();

        (await Reviews()).Should().BeEmpty();
    }

    [Fact]
    public async Task Running_twice_on_the_same_day_raises_one_review()
    {
        // The scheduler inserts unconditionally and this module has no inbox, so a job re-run by
        // hand would otherwise hand the officer a second copy of one task. Same business-level
        // guard the PEP consumer uses.
        await SeedFileAsync();
        AnniversariesToday(_clientId);

        await RunAsync();
        await RunAsync();

        (await Reviews()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Next_years_anniversary_is_owed_again_once_this_ones_is_completed()
    {
        // The counterpart: the guard is scoped to OPEN reviews, so the rule is annual rather than
        // once ever.
        await SeedFileAsync();
        AnniversariesToday(_clientId);
        await RunAsync();

        var first = await _db.KycReviewSchedules.AsTracking().IgnoreQueryFilters().SingleAsync();
        first.Complete(new FixedClock(Now)).IsSuccess.Should().BeTrue();
        await _db.SaveChangesAsync();

        await RunAsync();

        (await Reviews()).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_outage_reading_M01_loses_one_night_and_not_the_rule()
    {
        // Tomorrow's run looks at tomorrow's anniversaries. Throwing would have Hangfire retry
        // against an outage and fill the dashboard with a failure nobody can act on.
        await SeedFileAsync();
        _customers.GetClientsWithContractAnniversaryAsync(
                _tenant, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M01 is down"));

        var act = async () => await RunAsync();

        await act.Should().NotThrowAsync();
        (await Reviews()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        await SeedFileAsync();
        AnniversariesToday(_clientId);

        await new ContractAnniversaryReviewJob(
                new FixedScopeFactory(new ServiceCollection().BuildServiceProvider()),
                NullLogger<ContractAnniversaryReviewJob>.Instance)
            .RunAsync(Guid.Empty, CancellationToken.None);

        (await Reviews()).Should().BeEmpty();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
