namespace Sankore.Modules.Kyc.Tests.Features.Reviews;

using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Features.Reviews.Consumers;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// The consumer that turns M01's "this client's PEP exposure has risen" into an owed KYC review
/// (BNF-B-13). Driven as MassTransit would drive it, over the REAL
/// <c>RaiseKycReviewHandler</c> and <c>KycReviewScheduler</c> through a real MediatR container:
/// what is under test is whether a review ends up in the schedule table, once.
/// </summary>
public sealed class ClientPepExposureRaisedConsumerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _declaredBy = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly ServiceProvider _sp;
    private readonly ClientPepExposureRaisedConsumer _consumer;

    public ClientPepExposureRaisedConsumerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
        _sp = BuildContainer(_db);
        _consumer = new ClientPepExposureRaisedConsumer(
            new FixedScopeFactory(_sp),
            NullLogger<ClientPepExposureRaisedConsumer>.Instance);
    }

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
        _factory.Dispose();
    }

    private static ServiceProvider BuildContainer(KycDbContext db)
    {
        var services = new ServiceCollection();

        services.AddSingleton(db);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddKeyedSingleton(nameof(KycDbContext), Substitute.For<IEventPublisher>());

        // The real scheduler needs the tenant's review periodicity; an event-driven review is
        // due TODAY whatever it says, so any value serves.
        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);
        services.AddSingleton(settings);

        // The handler queues the tenant sweep so the agency is told today. Substituted: what the
        // sweep then does has its own tests.
        services.AddSingleton(Substitute.For<IBackgroundJobClient>());

        services.AddScoped<KycReviewScheduler>();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(KycModule).Assembly));

        return services.BuildServiceProvider();
    }

    private ClientPepExposureRaisedEvent Event(Guid? tenantId = null, Guid? clientId = null)
        => new(tenantId ?? _tenantId, clientId ?? _clientId, _declaredBy);

    private async Task<KycFile> SeedFileAsync(
        KycFileStatus status = KycFileStatus.Collecting, Guid? customerId = null)
    {
        var file = KycFile.Open(
            _tenantId, customerId ?? _clientId, KycChannel.Agency, _declaredBy, TimeProvider.System);

        if (status != KycFileStatus.Collecting)
            ForceStatus(file, status);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    /// <summary>
    /// Puts a file in a status the transition table does not allow us to walk to from
    /// <c>Collecting</c> in one step. The test is about what the consumer SELECTS, not about the
    /// state machine, which is pinned elsewhere.
    /// </summary>
    private static void ForceStatus(KycFile file, KycFileStatus status)
        => typeof(KycFile)
            .GetProperty(nameof(KycFile.Status))!
            .SetValue(file, status);

    private Task<List<KycReviewSchedule>> Reviews()
        => _db.KycReviewSchedules.IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task A_raised_exposure_schedules_one_review_due_today()
    {
        var file = await SeedFileAsync();

        await _consumer.Consume(Delivered.Of(Event()));

        var review = (await Reviews()).Should().ContainSingle().Subject;
        review.KycFileId.Should().Be(file.Id);
        review.Trigger.Should().Be(KycReviewTrigger.Event);
        review.Status.Should().Be(KycReviewStatus.Scheduled);
        review.DueDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task The_reason_carries_no_classification_detail()
    {
        // The reason is stored in clear and shown to an operator. The function, the country and
        // the family link stay behind the PEP read permission on the client record.
        await SeedFileAsync();

        await _consumer.Consume(Delivered.Of(Event()));

        var review = (await Reviews()).Should().ContainSingle().Subject;
        review.Reason.Should().Be(ClientPepExposureRaisedConsumer.PepReason);
        review.Reason.Should().NotContain(_clientId.ToString());
    }

    [Fact]
    public async Task A_redelivered_event_does_not_schedule_a_second_review()
    {
        // This module has no inbox table, so the guard is business-level: an OPEN event-driven
        // review carrying the same reason means the work is already owed. Without it, every
        // redelivery would hand the compliance officer another copy of the same task.
        await SeedFileAsync();

        await _consumer.Consume(Delivered.Of(Event()));
        await _consumer.Consume(Delivered.Of(Event()));
        await _consumer.Consume(Delivered.Of(Event()));

        (await Reviews()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_later_rise_is_owed_again_once_the_first_review_is_completed()
    {
        // The counterpart of the case above, and the reason the guard is scoped to OPEN reviews
        // rather than to "any review ever raised for this reason": exposure can rise again, and
        // the second rise is a fact of its own.
        await SeedFileAsync();
        await _consumer.Consume(Delivered.Of(Event()));

        // AsTracking, because this module's DbContext is NoTracking by default: mutating the
        // instance Reviews() hands back would change nothing in the store, and the test would
        // then be asserting against a review that is still open.
        var first = await _db.KycReviewSchedules.AsTracking().IgnoreQueryFilters().SingleAsync();
        first.Complete(TimeProvider.System).IsSuccess.Should().BeTrue();
        await _db.SaveChangesAsync();

        await _consumer.Consume(Delivered.Of(Event()));

        (await Reviews()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_client_with_no_open_file_is_not_an_incident()
    {
        // No file at all: nothing to review, and nothing a redelivery would fix.
        var act = async () => await _consumer.Consume(Delivered.Of(Event()));

        await act.Should().NotThrowAsync();
        (await Reviews()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(KycFileStatus.Rejected)]
    [InlineData(KycFileStatus.Suspended)]
    public async Task A_closed_file_is_not_selected(KycFileStatus status)
    {
        // KycFile.IsOpen is Ignore()d in the EF configuration, so the consumer compares statuses
        // explicitly. This pins that the two it excludes are the two IsOpen excludes.
        await SeedFileAsync(status);

        await _consumer.Consume(Delivered.Of(Event()));

        (await Reviews()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        await SeedFileAsync();

        await _consumer.Consume(Delivered.Of(Event(tenantId: Guid.Empty)));

        (await Reviews()).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_clients_file_is_never_reviewed()
    {
        // The selection is by customer, and getting it wrong would open a review on the wrong
        // person's file — which an officer would then read as a finding about them.
        await SeedFileAsync(customerId: Guid.NewGuid());

        await _consumer.Consume(Delivered.Of(Event()));

        (await Reviews()).Should().BeEmpty();
    }
}
