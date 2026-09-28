namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Lifecycle;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// Test doubles specific to the Lifecycle zone, on top of the module-wide ones in
/// <c>TestSupport/</c>: an event publisher that records instead of sending, an
/// inbox guard that really is idempotent (so replay tests exercise the contract and
/// not a substitute returning a canned sequence), and client builders that reach a
/// given status through the aggregate's own transitions.
/// </summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<IIntegrationEvent> _published = [];

    public IReadOnlyList<IIntegrationEvent> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory stand-in for <see cref="IInboxGuard"/> with the real semantics: the first
/// call for a given event id wins, every later one loses. Keyed on the event id ALONE,
/// like <c>InboxGuard</c>, whose uniqueness check runs with
/// <c>IgnoreQueryFilters()</c> against <c>inbox_messages</c>' primary key — which is
/// exactly why two consumers of the same event must claim distinct ids.
/// </summary>
internal sealed class FakeInboxGuard : IInboxGuard
{
    private readonly HashSet<Guid> _seen = [];

    public int Calls { get; private set; }

    public Task<bool> TryBeginAsync(Guid eventId, Guid tenantId, string eventType, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_seen.Add(eventId));
    }
}

internal static class LifecycleTestDoubles
{
    /// <summary>An <see cref="IHostEnvironment"/> whose only relevant fact is its name.</summary>
    internal static IHostEnvironment Environment(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        environment.ApplicationName.Returns("Sankore.Tests");
        return environment;
    }

    /// <summary>Agency directory that accepts every agency code and every advisor.</summary>
    internal static IAgencyDirectory PermissiveAgencyDirectory(string agencyCode = "AG000002")
    {
        var directory = Substitute.For<IAgencyDirectory>();
        directory.GetAgencyCodeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(agencyCode);
        directory.IsAdvisorEligibleAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return directory;
    }

    internal static IOutstandingBalanceProbe BalanceProbe(bool hasActiveCommitments)
    {
        var probe = Substitute.For<IOutstandingBalanceProbe>();
        probe.HasActiveCommitmentsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(hasActiveCommitments);
        return probe;
    }
}

/// <summary>
/// Lifecycle fixtures on top of <see cref="TestClientFactory"/>. A client can only be
/// born <c>PendingKyc</c>, so every other status is reached here through the aggregate's
/// OWN transitions rather than by poking fields: a fixture can therefore never encode a
/// state the domain would refuse, and each fixture arrives with the status-history lines
/// a real client would have.
/// </summary>
internal static class ClientBuilder
{
    internal static Client PendingKyc(
        Guid tenantId,
        Guid agencyId,
        Guid createdBy,
        Guid? advisorUserId = null)
        => TestClientFactory.Individual(
            tenantId, agencyId, advisorUserId: advisorUserId, createdBy: createdBy);

    internal static Client Active(
        Guid tenantId, Guid agencyId, Guid createdBy, Guid? advisorUserId = null)
    {
        var client = PendingKyc(tenantId, agencyId, createdBy, advisorUserId);
        client.ApplyKycValidated(DateTimeOffset.UtcNow, createdBy).IsSuccess.Should().BeTrue();
        return client;
    }

    internal static Client Suspended(
        Guid tenantId, Guid agencyId, Guid createdBy, Guid? advisorUserId = null)
    {
        var client = Active(tenantId, agencyId, createdBy, advisorUserId);
        client.Suspend("Compliance review", createdBy).IsSuccess.Should().BeTrue();
        return client;
    }

    internal static Client Archived(Guid tenantId, Guid agencyId, Guid createdBy)
    {
        var client = Active(tenantId, agencyId, createdBy);
        client.Archive("Client deceased", createdBy).IsSuccess.Should().BeTrue();
        return client;
    }
}
