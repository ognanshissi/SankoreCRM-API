namespace Sankore.Api.Tests.Features.ObjectStorage;

using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Api.Features.ObjectStorage.MigrateObjects;
using Sankore.Modules.Kyc;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// Four refusals, and each one exists because the alternative is a MISLEADING SUCCESS rather than
/// an error. Running this endpoint without a bucket, or against a folder that is not there, would
/// produce a clean report saying every object was accounted for — which an operator reads as "my
/// documents have moved" immediately before unmounting the volume that still holds them. That is
/// the failure these tests are about, and none of them is about the copy itself.
/// </summary>
public sealed class MigrateObjectsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperatorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _kycRoot = Path.Combine(
        Path.GetTempPath(), "sankore-migration-tests", Guid.NewGuid().ToString("N"));

    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();

    public void Dispose()
    {
        if (Directory.Exists(_kycRoot)) Directory.Delete(_kycRoot, recursive: true);
    }

    private MigrateObjectsHandler Sut(
        bool bucketConfigured = true,
        bool systemRole = true,
        bool sourceExists = true)
    {
        if (sourceExists) Directory.CreateDirectory(_kycRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kyc:Storage:BasePath"] = _kycRoot,
            })
            .Build();

        var services = new ServiceCollection();

        // The destination is what "a bucket is configured" means to this handler — it never reads
        // the R2 section itself. An InMemory backend stands in for R2: what matters is only that
        // it is NOT the filesystem the source is on.
        if (bucketConfigured)
            services.AddKeyedSingleton<IObjectBackend>(
                KycModule.ObjectStorageConcern, new InMemoryObjectBackend());
        else
            services.AddKeyedSingleton<IObjectBackend>(
                KycModule.ObjectStorageConcern,
                new LocalObjectBackend(_kycRoot, NullLogger<LocalObjectBackend>.Instance));

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(OperatorId);
        currentUser.TenantId.Returns(TenantId);
        currentUser.Roles.Returns(systemRole
            ? [Roles.System.Name]
            : [Roles.Administrator.Name]);

        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());

        return new MigrateObjectsHandler(
            services.BuildServiceProvider(), config, environment, currentUser, _jobs,
            NullLogger<MigrateObjectsHandler>.Instance);
    }

    private async Task<Result<MigrateObjectsResult>> Run(
        MigrateObjectsHandler sut, string? concern = null)
        => await sut.Handle(new MigrateObjectsCommand(concern ?? KycModule.ObjectStorageConcern), default);

    private void NothingWasEnqueued() =>
        _jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());

    [Fact]
    public async Task An_administrator_of_one_tenant_must_not_start_a_platform_wide_copy()
    {
        var result = await Run(Sut(systemRole: false));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("OBJECT_STORAGE_MIGRATION_REQUIRES_SYSTEM_ROLE");
        NothingWasEnqueued();
    }

    [Fact]
    public async Task An_unknown_concern_should_be_refused_and_name_the_ones_that_exist()
    {
        var result = await Run(Sut(), concern: "sdk");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("OBJECT_STORAGE_CONCERN_UNKNOWN")
            .And.Contain(KycModule.ObjectStorageConcern,
                "the message has to say what a caller may ask for instead");
        NothingWasEnqueued();
    }

    [Fact]
    public async Task With_no_bucket_configured_it_must_refuse_rather_than_copy_onto_itself()
    {
        var result = await Run(Sut(bucketConfigured: false));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("OBJECT_STORAGE_NOT_CONFIGURED");
        NothingWasEnqueued();
    }

    [Fact]
    public async Task A_source_folder_that_does_not_exist_must_be_reported_not_enqueued()
    {
        var result = await Run(Sut(sourceExists: false));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("OBJECT_STORAGE_SOURCE_EMPTY");
        NothingWasEnqueued();
    }

    [Fact]
    public async Task An_accepted_request_should_enqueue_the_copy_and_echo_the_resolved_source()
    {
        var result = await Run(Sut());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Concern.Should().Be(KycModule.ObjectStorageConcern);
        result.Value.SourceBasePath.Should().Be(_kycRoot,
            "an operator confirms the server meant their folder before a byte is read");

        _jobs.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(MigrateObjectsJob)
                             && j.Args.Contains(_kycRoot)
                             && j.Args.Contains(OperatorId)),
            Arg.Any<IState>());
    }
}
