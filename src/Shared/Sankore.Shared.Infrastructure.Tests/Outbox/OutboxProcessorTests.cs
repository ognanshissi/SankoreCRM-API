namespace Sankore.Shared.Infrastructure.Tests.Outbox;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The retry accounting, which is what decides whether an event is ever delivered.
///
/// A message stops being published once <c>RetryCount</c> reaches the maximum — the pending query
/// filters on it — and nothing announces that. So what does and does not consume an attempt is the
/// whole behaviour worth pinning: a cancelled publish is a host shutting down, not a failure, and
/// counting it meant five restarts could retire an event that had never actually failed.
/// </summary>
public sealed class OutboxProcessorTests
{
    private sealed record ProbeEvent(string Payload) : IntegrationEventBase;

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OutboxMessage>().HasKey(m => m.Id);
        }
    }

    private static ProbeDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<ProbeDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static OutboxMessage Pending() => new()
    {
        Id = Guid.NewGuid(),
        EventType = typeof(ProbeEvent).AssemblyQualifiedName!,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(
            new ProbeEvent("hello"), OutboxJson.Options),
        OccurredAt = DateTimeOffset.UtcNow,
        RetryCount = 0,
    };

    /// <summary>
    /// The processor resolves its DbContext and IBus from a scope, so the test supplies a
    /// container holding the same instances it will assert against.
    /// </summary>
    private static (OutboxProcessor<ProbeDbContext> Processor, IServiceProvider Sp) Build(
        string databaseName, IBus bus)
    {
        var sp = new ServiceCollection()
            // Scoped over the same in-memory database, as in production: the processor opens its
            // own scope, so it must not share the context the test seeded with — otherwise the
            // seeded entity is still tracked and Update() collides on the key.
            .AddScoped(_ => NewContext(databaseName))
            .AddSingleton(bus)
            .BuildServiceProvider();

        return (new OutboxProcessor<ProbeDbContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessor<ProbeDbContext>>.Instance), sp);
    }

    private static Task ProcessOnceAsync(
        OutboxProcessor<ProbeDbContext> processor, CancellationToken ct) =>
        (Task)typeof(OutboxProcessor<ProbeDbContext>)
            .GetMethod("ProcessBatchAsync", System.Reflection.BindingFlags.NonPublic
                                          | System.Reflection.BindingFlags.Instance)!
            .Invoke(processor, [ct])!;

    [Fact]
    public async Task A_published_message_is_marked_processed()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewContext(name);
        var message = Pending();
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();

        var bus = Substitute.For<IBus>();
        var (processor, _) = Build(name, bus);

        await ProcessOnceAsync(processor, CancellationToken.None);

        await using var verify = NewContext(name);
        var saved = verify.Set<OutboxMessage>().Single();
        saved.ProcessedAt.Should().NotBeNull();
        saved.RetryCount.Should().Be(0);
        saved.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_publish_does_not_consume_an_attempt()
    {
        // The regression. A host stopped mid-send used to leave RetryCount incremented and
        // LastError "A task was canceled." — five of those and the message was retired without
        // ever having failed.
        var name = Guid.NewGuid().ToString();
        await using var db = NewContext(name);
        db.Set<OutboxMessage>().Add(Pending());
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();

        var bus = Substitute.For<IBus>();
        bus.Publish(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Exactly what a shutdown during a RabbitMQ publish produces.
                cts.Cancel();
                throw new TaskCanceledException();
            });

        var (processor, _) = Build(name, bus);

        await ProcessOnceAsync(processor, cts.Token);

        await using var verify = NewContext(name);
        var saved = verify.Set<OutboxMessage>().Single();
        saved.ProcessedAt.Should().BeNull("the publish did not happen");
        saved.RetryCount.Should().Be(
            0, "a shutdown is not a delivery failure and must not spend an attempt");
        saved.LastError.Should().BeNull("'A task was canceled.' is not a diagnosis worth keeping");
    }

    [Fact]
    public async Task A_real_failure_still_consumes_an_attempt_and_records_why()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewContext(name);
        db.Set<OutboxMessage>().Add(Pending());
        await db.SaveChangesAsync();

        var bus = Substitute.For<IBus>();
        bus.Publish(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("broker refused it"));

        var (processor, _) = Build(name, bus);

        await ProcessOnceAsync(processor, CancellationToken.None);

        await using var verify = NewContext(name);
        var saved = verify.Set<OutboxMessage>().Single();
        saved.ProcessedAt.Should().BeNull();
        saved.RetryCount.Should().Be(1);
        saved.LastError.Should().Be("broker refused it");
    }

    [Fact]
    public async Task Cancellation_before_the_batch_starts_touches_nothing()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewContext(name);
        db.Set<OutboxMessage>().Add(Pending());
        await db.SaveChangesAsync();

        var bus = Substitute.For<IBus>();
        var (processor, _) = Build(name, bus);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The pending query itself observes the token, so the batch never starts. ExecuteAsync's
        // own `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`
        // is what turns this into a clean stop.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessOnceAsync(processor, cts.Token));

        await using var verify = NewContext(name);
        var saved = verify.Set<OutboxMessage>().Single();
        saved.RetryCount.Should().Be(0);
        saved.ProcessedAt.Should().BeNull();

        await bus.DidNotReceive().Publish(
            Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_at_the_retry_ceiling_is_no_longer_picked_up()
    {
        // Pins the consequence the new error log announces: the row simply stops being selected.
        var name = Guid.NewGuid().ToString();
        await using var db = NewContext(name);
        var exhausted = Pending();
        exhausted.RetryCount = 5;
        exhausted.LastError = "broker refused it";
        db.Set<OutboxMessage>().Add(exhausted);
        await db.SaveChangesAsync();

        var bus = Substitute.For<IBus>();
        var (processor, _) = Build(name, bus);

        await ProcessOnceAsync(processor, CancellationToken.None);

        await bus.DidNotReceive().Publish(
            Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>());
    }
}
