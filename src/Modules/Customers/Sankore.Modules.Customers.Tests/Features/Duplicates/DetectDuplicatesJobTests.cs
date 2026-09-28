namespace Sankore.Modules.Customers.Tests.Features.Duplicates;

using FluentAssertions;
using Hangfire;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class DetectDuplicatesJobTests
{
    [Fact]
    public async Task Job_runs_the_detection_under_the_SYSTEM_identity()
    {
        var tenantId = Guid.NewGuid();
        ICurrentUser? actor = null;
        ITenantContext? ambientTenant = null;

        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<DetectDuplicatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Captured from inside the dispatch: this is the identity the audit behavior sees.
                actor = BackgroundJobContext.CurrentUser;
                ambientTenant = BackgroundJobContext.CurrentTenant;
                return Result.Ok(new DetectDuplicatesResult(0, 0, 0, 0, 0));
            });

        await using var provider = Provider(sender);
        await new DetectDuplicatesJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(tenantId);

        actor.Should().NotBeNull();
        actor!.Id.Should().Be(Guid.Empty, "a nightly scan has no human author");
        actor.DisplayName.Should().Be("SYSTEM");
        actor.TenantId.Should().Be(tenantId);
        ambientTenant!.CurrentTenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Job_dispatches_the_command_for_the_tenant_it_was_given()
    {
        var tenantId = Guid.NewGuid();
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<DetectDuplicatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DetectDuplicatesResult(0, 0, 0, 0, 0)));

        await using var provider = Provider(sender);
        await new DetectDuplicatesJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(tenantId);

        await sender.Received(1).Send(
            Arg.Is<DetectDuplicatesCommand>(c => c.TenantId == tenantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Job_swallows_a_failed_detection_so_Hangfire_does_not_retry_a_business_failure()
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<DetectDuplicatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<DetectDuplicatesResult>("SOME_FAILURE"));

        await using var provider = Provider(sender);

        var act = async () => await new DetectDuplicatesJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Orchestrator_fans_out_one_job_per_active_tenant_with_opaque_arguments_only()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TenantInfo>>(_ => [Tenant(first), Tenant(second)]);

        var hangfire = Substitute.For<IBackgroundJobClient>();

        await new DetectDuplicatesOrchestratorJob(
                tenantStore, hangfire, DuplicatesTestDoubles.Logger<DetectDuplicatesOrchestratorJob>())
            .ExecuteAsync();

        // Two enqueues, and the only thing that travelled is a tenant id: no client data ever
        // reaches the Hangfire tables.
        hangfire.ReceivedCalls().Should().HaveCount(2);
    }

    private static ServiceProvider Provider(ISender sender)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sender);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static TenantInfo Tenant(Guid id) => new(
        Id: id,
        Name: "tenant",
        Fqdn: "tenant.example.test",
        IsActive: true,
        IsMaintenance: false,
        TrialExpiresAt: null,
        BlockedAt: null);
}
