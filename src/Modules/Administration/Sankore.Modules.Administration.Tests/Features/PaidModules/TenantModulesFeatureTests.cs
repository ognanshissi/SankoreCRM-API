namespace Sankore.Modules.Administration.Tests.Features.PaidModules;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.PaidModules;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The tenant administrator's side of the paid modules: always the caller's own tenant and name,
/// the registry's codes relayed as they are, the cached tenant dropped after a change.
/// </summary>
public sealed class TenantModulesFeatureTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly FakeRegistry _registry = new();
    private readonly ITenantCacheInvalidator _cache = Substitute.For<ITenantCacheInvalidator>();
    private readonly Guid _userId;
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private string[] _platformRecipients = ["ops@sankore.ci", "facturation@sankore.ci"];

    private TenantModuleNotifier Notifier(AdministrationDbContext db) =>
        new(_notifications, _tenantStore, db,
            Options.Create(new TenantModuleNotificationOptions { PlatformRecipients = _platformRecipients }),
            NullLogger<TenantModuleNotifier>.Instance);

    private RequestTenantModuleHandler RequestHandler(AdministrationDbContext db) =>
        new(_registry, _currentUser, db, _cache, Notifier(db));

    private DeactivateTenantModuleHandler DeactivateHandler(AdministrationDbContext db) =>
        new(_registry, _currentUser, db, _cache, Notifier(db), TimeProvider.System);

    private List<QueueEmailRequest> QueuedEmails() =>
        [.. _notifications.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(INotificationsModule.QueueEmailAsync))
            .Select(c => (QueueEmailRequest)c.GetArguments()[0]!)];

    public TenantModulesFeatureTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Awa", "Koné", "awa@caisse.ci");
        db.Users.Add(user);
        db.SaveChanges();
        _userId = user.Id;

        _currentUser.Id.Returns(_userId);
        _currentUser.TenantId.Returns(_tenantId);
        _tenantStore.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Caisse Avenir", "https://crm.caisse.ci", true, false, null, null));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_request_goes_out_for_the_callers_tenant_in_the_callers_name_and_refreshes_the_cache()
    {
        var subscriptionId = Guid.NewGuid();
        _registry.RequestAnswer = new RegistryResult<Guid>(subscriptionId, null);

        await using var db = _factory.CreateContext();
        var result = await RequestHandler(db)
            .Handle(new RequestTenantModuleCommand(TenantModules.Insurance, 150000m, "XOF"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(subscriptionId);
        _registry.Calls.Should().Equal($"request {_tenantId} Insurance {_userId} awa@caisse.ci 150000 XOF");
        await _cache.Received(1).InvalidateAsync(_tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Something_that_is_not_a_module_never_reaches_the_registry()
    {
        await using var db = _factory.CreateContext();
        var result = await RequestHandler(db)
            .Handle(new RequestTenantModuleCommand("Telephony", 0m, "XOF"), default);

        result.Error.Should().Be(TenantModulesErrors.ModuleUnknown);
        _registry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_registry_refusal_is_relayed_and_the_cache_left_alone()
    {
        _registry.RequestAnswer = new RegistryResult<Guid>(default, "MODULE_PRICE_CHANGED", "Current price: 200000 XOF.");

        await using var db = _factory.CreateContext();
        var result = await RequestHandler(db)
            .Handle(new RequestTenantModuleCommand(TenantModules.Insurance, 150000m, "XOF"), default);

        result.Error.Should().Be("MODULE_PRICE_CHANGED");
        await _cache.DidNotReceiveWithAnyArgs().InvalidateAsync(default, default);
    }

    [Fact]
    public async Task A_deactivation_takes_effect_on_the_next_request()
    {

        await using var db = _factory.CreateContext();
        var result = await DeactivateHandler(db)
            .Handle(new DeactivateTenantModuleCommand(TenantModules.BankingMicrofinance), default);

        result.IsSuccess.Should().BeTrue();
        _registry.Calls.Should().Equal($"deactivate {_tenantId} BankingMicrofinance {_userId} awa@caisse.ci");
        await _cache.Received(1).InvalidateAsync(_tenantId, Arg.Any<CancellationToken>());
    }

    // ── Lot 4: who is told ──────────────────────────────────────────────────

    [Fact]
    public async Task A_request_mails_every_platform_recipient_with_the_accepted_price()
    {
        var subscriptionId = Guid.NewGuid();
        _registry.RequestAnswer = new RegistryResult<Guid>(subscriptionId, null);

        await using var db = _factory.CreateContext();
        await RequestHandler(db).Handle(new RequestTenantModuleCommand(TenantModules.Insurance, 150000m, "XOF"), default);

        var mails = QueuedEmails();
        mails.Select(m => m.RecipientEmail).Should().BeEquivalentTo(_platformRecipients);
        mails.Should().OnlyContain(m => m.TemplateKey == TenantModuleNotifier.RequestedTemplate
                                        && m.TenantId == _tenantId
                                        && m.IdempotencyKey.Contains(subscriptionId.ToString()));
        mails[0].TemplateData["company_name"].Should().Be("Caisse Avenir");
        mails[0].TemplateData["module_label"].Should().Be("Assurance");
        mails[0].TemplateData["requested_by"].Should().Be("awa@caisse.ci");
        ((string)mails[0].TemplateData["accepted_price"]).Should().EndWith("XOF");
    }

    [Fact]
    public async Task A_refused_request_mails_nobody()
    {
        _registry.RequestAnswer = new RegistryResult<Guid>(default, "MODULE_REQUEST_PENDING");

        await using var db = _factory.CreateContext();
        await RequestHandler(db).Handle(new RequestTenantModuleCommand(TenantModules.Insurance, 150000m, "XOF"), default);

        QueuedEmails().Should().BeEmpty();
    }

    [Fact]
    public async Task A_deactivation_tells_the_platform_and_no_recipient_configured_is_not_an_error()
    {
        await using var db = _factory.CreateContext();
        (await DeactivateHandler(db).Handle(new DeactivateTenantModuleCommand(TenantModules.Insurance), default))
            .IsSuccess.Should().BeTrue();
        QueuedEmails().Should().HaveCount(2)
            .And.OnlyContain(m => m.TemplateKey == TenantModuleNotifier.DeactivatedTemplate);

        _notifications.ClearReceivedCalls();
        _platformRecipients = [];
        (await DeactivateHandler(db).Handle(new DeactivateTenantModuleCommand(TenantModules.Insurance), default))
            .IsSuccess.Should().BeTrue("the act stands whoever is told");
        QueuedEmails().Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancellation_names_the_callers_tenant_so_another_tenants_row_reads_as_absent()
    {
        var subscriptionId = Guid.NewGuid();
        var result = await new CancelTenantModuleRequestHandler(_registry, _currentUser)
            .Handle(new CancelTenantModuleRequestCommand(subscriptionId), default);

        result.IsSuccess.Should().BeTrue();
        _registry.Calls.Should().Equal($"cancel {_tenantId} {subscriptionId} {_userId}");
    }

    [Theory]
    [InlineData("TENANT_REGISTRY_UNAVAILABLE", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("MODULE_UNKNOWN", StatusCodes.Status404NotFound)]
    [InlineData("MODULE_SUBSCRIPTION_NOT_FOUND", StatusCodes.Status404NotFound)]
    [InlineData("INVALID_ACTOR", StatusCodes.Status400BadRequest)]
    [InlineData("MODULE_PRICE_CHANGED", StatusCodes.Status409Conflict)]
    [InlineData("MODULE_ALREADY_ACTIVE", StatusCodes.Status409Conflict)]
    public void Each_code_maps_to_its_status(string error, int status)
    {
        TenantModulesErrors.ToProblem(error).Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(status);
    }
}

/// <summary>Records what the handlers ask the registry; the interface is internal, so no NSubstitute proxy.</summary>
internal sealed class FakeRegistry : ITenantModuleRegistry
{
    public List<string> Calls { get; } = [];
    public RegistryResult<Guid> RequestAnswer { get; set; } = new(Guid.NewGuid(), null);
    public IReadOnlyList<RegistryTenantModule> Modules { get; set; } = [];

    public Task<RegistryResult<RegistryTenantModules>> ListAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(new RegistryResult<RegistryTenantModules>(new RegistryTenantModules(tenantId, Modules), null));

    public Task<RegistryResult<Guid>> RequestAsync(Guid tenantId, string module, Guid userId, string email,
        decimal acceptedMonthlyPrice, string acceptedCurrency, CancellationToken ct)
    {
        Calls.Add($"request {tenantId} {module} {userId} {email} {acceptedMonthlyPrice} {acceptedCurrency}");
        return Task.FromResult(RequestAnswer);
    }

    public Task<RegistryResult<bool>> CancelAsync(Guid tenantId, Guid subscriptionId, Guid userId, CancellationToken ct)
    {
        Calls.Add($"cancel {tenantId} {subscriptionId} {userId}");
        return Task.FromResult(new RegistryResult<bool>(true, null));
    }

    public Task<RegistryResult<bool>> DeactivateAsync(Guid tenantId, string module, Guid userId, string email,
        CancellationToken ct)
    {
        Calls.Add($"deactivate {tenantId} {module} {userId} {email}");
        return Task.FromResult(new RegistryResult<bool>(true, null));
    }
}

/// <summary>The HTTP client: what the registry answers, and what an outage becomes.</summary>
public sealed class HttpTenantModuleRegistryTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return answer(request);
        }
    }

    private static (HttpTenantModuleRegistry Registry, StubHandler Handler) Registry(
        Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new StubHandler(answer);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(TenantStoreServiceCollectionExtensions.HttpClientName)
            .Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://registry/") });
        return (new HttpTenantModuleRegistry(factory, NullLogger<HttpTenantModuleRegistry>.Instance), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_request_posts_the_actor_and_the_accepted_price_to_the_tenants_route()
    {
        var tenantId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var (registry, handler) = Registry(_ => Json(HttpStatusCode.Created, $$"""{"id":"{{id}}"}"""));

        var result = await registry.RequestAsync(tenantId, TenantModules.Insurance, Guid.NewGuid(), "a@b.c",
            150000m, "XOF", default);

        result.Value.Should().Be(id);
        handler.Last!.RequestUri!.AbsolutePath.Should().Be($"/api/v1/tenants/{tenantId}/modules/Insurance/requests");
        handler.LastBody.Should().Contain("\"acceptedMonthlyPrice\":150000").And.Contain("\"email\":\"a@b.c\"");
    }

    [Fact]
    public async Task A_business_refusal_is_relayed_with_its_code_and_detail()
    {
        var (registry, _) = Registry(_ => Json(HttpStatusCode.Conflict,
            """{"error":"MODULE_PRICE_CHANGED","detail":"Current price: 200000 XOF."}"""));

        var result = await registry.RequestAsync(Guid.NewGuid(), TenantModules.Insurance, Guid.NewGuid(), "a@b.c",
            150000m, "XOF", default);

        result.Error.Should().Be("MODULE_PRICE_CHANGED");
        result.Detail.Should().Contain("200000");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_registry_failure_or_a_wrong_service_key_is_our_outage_not_the_tenants_mistake(HttpStatusCode status)
    {
        var (registry, _) = Registry(_ => Json(status, """{"error":"A valid X-Api-Key header is required."}"""));

        (await registry.DeactivateAsync(Guid.NewGuid(), TenantModules.Insurance, Guid.NewGuid(), "a@b.c", default))
            .Error.Should().Be(HttpTenantModuleRegistry.Unavailable);
    }

    [Fact]
    public async Task An_unreachable_registry_is_reported_as_unavailable()
    {
        var (registry, _) = Registry(_ => throw new HttpRequestException("connection refused"));

        (await registry.ListAsync(Guid.NewGuid(), default)).Error.Should().Be(HttpTenantModuleRegistry.Unavailable);
    }

    [Fact]
    public async Task The_list_is_read_with_its_states_as_names()
    {
        var (registry, _) = Registry(_ => Json(HttpStatusCode.OK, """
            {"tenantId":"6f0c3c0e-0000-0000-0000-000000000001","modules":[
              {"code":"Insurance","label":"Assurance","description":"","monthlyPrice":150000,"currency":"XOF",
               "isAvailable":true,"state":"Requested",
               "current":{"id":"6f0c3c0e-0000-0000-0000-000000000002","tenantId":"6f0c3c0e-0000-0000-0000-000000000001",
                 "moduleCode":"Insurance","status":"Requested","source":"Request","createdAt":"2026-10-23T09:00:00Z",
                 "requestedAt":"2026-10-23T09:00:00Z","requestedByEmail":"a@b.c","acceptedMonthlyPrice":150000,
                 "acceptedCurrency":"XOF","decidedByAdminId":null},
               "history":[]}]}
            """));

        var result = await registry.ListAsync(Guid.NewGuid(), default);

        var module = result.Value!.Modules.Single().ToDto();
        module.State.Should().Be(TenantModuleState.Requested);
        module.Current!.Status.Should().Be(TenantModuleState.Requested);
        module.Current.AcceptedMonthlyPrice.Should().Be(150000m);
    }
}
