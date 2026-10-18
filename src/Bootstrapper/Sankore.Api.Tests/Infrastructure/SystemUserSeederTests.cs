namespace Sankore.Api.Tests.Infrastructure;

using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Api.Infrastructure;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Register;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// When the start-up seeder provisions a system user, and — far more important — when it does
/// not.
///
/// <para>
/// <b>Why this suite exists.</b> This code runs unattended on every boot of every deployment,
/// and it creates a super-user. Two properties have to hold or it becomes a liability: a redeploy
/// must write nothing, and nothing it finds wrong may stop the host from starting, because an
/// API that refuses to boot does not answer <c>/health</c> either and Dokploy turns that into a
/// restart loop. Every case below is one of the ways it is asked to do nothing.
/// </para>
/// </summary>
public sealed class SystemUserSeederTests
{
    private const string Fqdn = "https://crm.example.com";
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Provisions_the_system_user_when_the_tenant_has_none()
    {
        var harness = new Harness();

        await SystemUserSeeder.SeedAsync(harness.Services);

        var command = harness.SentCommand();
        command.Should().NotBeNull();
        command!.TenantId.Should().Be(TenantId);
        command.Email.Should().Be("admin@example.com");
        // The handler validates that the two match; sending them apart would be a 400 at boot.
        command.ConfirmPassword.Should().Be(command.Password);
    }

    [Fact]
    public async Task Writes_nothing_on_a_redeploy()
    {
        var harness = new Harness();
        await harness.GivenSystemUserExistsAsync();

        await SystemUserSeeder.SeedAsync(harness.Services);

        harness.SentCommand().Should().BeNull(
            "a tenant may hold one system user, and a second boot must not try for another");
    }

    [Fact]
    public async Task Does_nothing_at_all_without_a_password()
    {
        var harness = new Harness(o => o.Password = string.Empty);

        await SystemUserSeeder.SeedAsync(harness.Services);

        harness.SentCommand().Should().BeNull();
        _ = harness.TenantStore.DidNotReceiveWithAnyArgs().GetByFqdnAsync(default!, default);
    }

    [Theory]
    // The bare host docs/deployment-dokploy.md used to recommend: it can never match the
    // location.origin clients send, so storing it would produce a deployment that looks correct
    // and answers 400 on every request.
    [InlineData("crm.example.com")]
    [InlineData("")]
    public async Task Refuses_a_tenant_fqdn_that_is_not_an_origin(string fqdn)
    {
        var harness = new Harness(o => o.TenantFqdn = fqdn);

        await SystemUserSeeder.SeedAsync(harness.Services);

        harness.SentCommand().Should().BeNull();
        _ = harness.TenantStore.DidNotReceiveWithAnyArgs().GetByFqdnAsync(default!, default);
    }

    [Fact]
    public async Task Reports_a_password_the_identity_policy_would_reject_instead_of_sending_it()
    {
        var harness = new Harness(o => o.Password = "short");

        await SystemUserSeeder.SeedAsync(harness.Services);

        harness.SentCommand().Should().BeNull();
    }

    [Fact]
    public async Task Gives_up_without_throwing_when_the_registry_never_answers()
    {
        var harness = new Harness();
        harness.TenantStore.GetByFqdnAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TenantInfo?)null);

        // Two attempts with no delay: the real loop waits half a minute, and what is under
        // test is that it returns rather than how long it waits.
        var act = async () => await SystemUserSeeder.SeedAsync(
            harness.Services, maxAttempts: 2, retryDelay: TimeSpan.Zero);

        await act.Should().NotThrowAsync();
        harness.SentCommand().Should().BeNull();
    }

    private sealed class Harness
    {
        public ITenantStore TenantStore { get; } = Substitute.For<ITenantStore>();

        private readonly ISender _sender = Substitute.For<ISender>();
        private readonly string _databaseName = $"api-seeder-tests-{Guid.NewGuid()}";

        public IServiceProvider Services { get; }

        public Harness(Action<SystemUserOptions>? configure = null)
        {
            TenantStore
                .GetByFqdnAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TenantInfo(
                    TenantId, "Example", Fqdn, IsActive: true, IsMaintenance: false,
                    TrialExpiresAt: null, BlockedAt: null));

            _sender
                .Send(Arg.Any<RegisterCommand>(), Arg.Any<CancellationToken>())
                .Returns(Result.Ok(new RegisterResult(Guid.NewGuid())));

            var services = new ServiceCollection();

            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(TenantStore);
            services.AddSingleton(_sender);
            services.AddSingleton(Substitute.For<IHttpClientFactory>());

            services.Configure<SystemUserOptions>(o =>
            {
                o.TenantFqdn = Fqdn;
                o.Email = "admin@example.com";
                o.Password = "Str0ng!Passw0rd";
                configure?.Invoke(o);
            });

            // The policy AdministrationModule actually configures: a length and a digit, the
            // rest left at Identity's defaults.
            services.Configure<IdentityOptions>(o =>
            {
                o.Password.RequireDigit = true;
                o.Password.RequiredLength = 8;
            });

            services.AddScoped(_ => CreateContext());

            Services = services.BuildServiceProvider();
        }

        public AdministrationDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AdministrationDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options;

            return new AdministrationDbContext(options, new FixedTenantContext(TenantId));
        }

        public async Task GivenSystemUserExistsAsync()
        {
            await using var db = CreateContext();
            db.Users.Add(AppUser.CreateRoot(TenantId, "Already", "There", "root@example.com"));
            await db.SaveChangesAsync();
        }

        public RegisterCommand? SentCommand() =>
            _sender.ReceivedCalls()
                .Select(c => c.GetArguments().FirstOrDefault())
                .OfType<RegisterCommand>()
                .FirstOrDefault();
    }
}
