namespace Sankore.Api.Tests.Features;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Api.Features.TestMode;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Sandbox lot 5, « Repartir de la production »: the twin is emptied when asked, then every copier
/// runs from production into it in dependency order, as the user, and the user is told — and nothing
/// happens to a tenant the registry does not tie to that production.
/// </summary>
public sealed class RefreshTestTwinJobTests
{
    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly ITenantDataWipe _wipe = Substitute.For<ITenantDataWipe>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly List<string> _journal = [];

    public RefreshTestTwinJobTests()
        => _tenants.GetAsync(_twin, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_twin, "Caisse (test)", "caisse.ci", true, false, null, null, IsTest: true, ParentTenantId: _live));

    private RefreshTestTwinJob Job()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_wipe);
        services.AddSingleton(_notifications);
        // Registered out of order on purpose: the job sorts.
        services.AddSingleton<IConfigurationCopier>(new RecordingCopier("project-template", 30, _journal, fails: true));
        services.AddSingleton<IConfigurationCopier>(new RecordingCopier("product", 10, _journal));
        return new RefreshTestTwinJob(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _tenants,
            NullLogger<RefreshTestTwinJob>.Instance);
    }

    [Fact]
    public async Task Production_is_copied_into_the_twin_in_dependency_order_as_the_user_and_the_user_is_told()
    {
        await Job().ExecuteAsync(_twin, _live, _user, "Awa", wipeData: true);

        await _wipe.Received(1).WipeAsync(_twin, Arg.Any<CancellationToken>());
        _journal.Should().Equal($"product {_live}→{_twin} as {_user}", $"project-template {_live}→{_twin} as {_user}");
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(n => n.TenantId == _twin && n.UserId == _user
                && n.Body!.Contains("1 éléments repris") && n.Body.Contains("1 non repris")
                && n.Body.Contains("1 gabarit de projet (PROJECT_TEMPLATE_UNKNOWN_PRODUCT)") && n.Body.Contains("vidées")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_the_wipe_the_trial_data_stay()
    {
        await Job().ExecuteAsync(_twin, _live, _user, "Awa", wipeData: false);

        await _wipe.DidNotReceiveWithAnyArgs().WipeAsync(default, default);
    }

    [Fact]
    public async Task A_tenant_the_registry_does_not_tie_to_that_production_is_left_alone()
    {
        await Job().ExecuteAsync(_twin, Guid.NewGuid(), _user, "Awa", wipeData: true);

        await _wipe.DidNotReceiveWithAnyArgs().WipeAsync(default, default);
        _journal.Should().BeEmpty();
    }

    [Fact]
    public void Refusals_are_told_in_words_grouped_by_kind_and_reason()
        => RefreshTestTwinJob.Describe([
                ("project-template", "PROJECT_STEP_QUESTIONNAIRE_FORM_MISSING"),
                ("project-template", "PROJECT_STEP_QUESTIONNAIRE_FORM_MISSING"),
                ("webhook", ""),
            ])
            .Should().Be("2 gabarit de projet (il exige un questionnaire publié, absent du mode test) ; 1 webhook (erreur inattendue, voir les journaux)");

    private sealed class RecordingCopier(string kind, int order, List<string> journal, bool fails = false) : IConfigurationCopier
    {
        public string Kind => kind;
        public int Order => order;

        public Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);

        public Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
        {
            copy.ToProduction.Should().BeFalse();
            // The job's own scope is the twin's, as the user: what the copiers read their context from.
            BackgroundJobContext.CurrentTenant!.CurrentTenantId.Should().Be(copy.TargetTenantId);
            journal.Add($"{kind} {copy.SourceTenantId}→{copy.TargetTenantId} as {copy.UserId}");
            return Task.FromResult(fails
                ? Result.Fail<PromotionResult>("PROJECT_TEMPLATE_UNKNOWN_PRODUCT")
                : Result.Ok(new PromotionResult(PromotionActions.Created, Guid.NewGuid(), [], [])));
        }
    }
}
