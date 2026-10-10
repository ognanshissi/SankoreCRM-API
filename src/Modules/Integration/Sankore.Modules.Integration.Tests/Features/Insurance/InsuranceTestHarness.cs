namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Doubles and builders shared by the insurance slice tests. Everything is built with
/// <c>new</c> — no container, no host, no MediatR pipeline — the shape
/// <c>ConnectionsTestHarness</c> settled on: a handler test that needed DI would be testing the
/// wiring instead of the decision.
/// </summary>
internal static class InsuranceTestHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 5, 20, 10, 0, 0, TimeSpan.Zero);

    internal static readonly DateOnly Today = new(2026, 5, 20);

    internal static readonly Guid Actor = Guid.Parse("55555555-5555-5555-5555-555555555555");

    internal static readonly Guid Customer = Guid.Parse("66666666-6666-6666-6666-666666666666");

    internal static TimeProvider Clock(DateTimeOffset? at = null) => new FixedClock(at ?? Now);

    internal static ICurrentUser User(Guid tenantId, Guid? userId = null)
        => new TestCurrentUser(userId ?? Actor, tenantId);

    internal static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    /// <summary>The CRM catalogue code the insurance doubles resolve by default.</summary>
    internal const string CrmCode = "ASS-VIE";

    /// <summary>
    /// An <see cref="IAdministrationModule"/> that resolves one credit product code to one
    /// category and one CRM catalogue code to one product.
    ///
    /// <para>
    /// The defaults are what each check wants to pass: "CRED-001" is a Loan, which is what a
    /// borrower's-insurance product needs, and <see cref="CrmCode"/> is an active Insurance entry,
    /// which is what a linked distribution agreement needs. Pass a different category — or
    /// <c>crmActive: false</c> — to exercise the refusals.
    /// </para>
    ///
    /// <para>
    /// Any OTHER code answers null, which is the substitute's own default and the honest one: a
    /// code the tenant's catalogue does not contain. That is what keeps the products seeded with no
    /// CRM code at all unaffected — they never reach a lookup.
    /// </para>
    /// </summary>
    internal static IAdministrationModule Administration(
        string code = "CRED-001",
        string? category = "Loan",
        string crmCode = CrmCode,
        string? crmCategory = "Insurance",
        bool crmActive = true)
    {
        var module = Substitute.For<IAdministrationModule>();

        module.GetProductCategoryAsync(Arg.Any<Guid>(), code, Arg.Any<CancellationToken>())
            .Returns(category);

        if (crmCategory is not null)
            module.GetProductAsync(Arg.Any<Guid>(), crmCode, Arg.Any<CancellationToken>())
                .Returns(new ProductSummary(crmCode, "Assurance vie", crmCategory, crmActive));

        return module;
    }

    internal static LinkedCreditProductCheck CreditCheck(
        Guid tenantId, IAdministrationModule? administration = null)
        => new(
            administration ?? Administration(),
            new FixedTenantContext(tenantId),
            Log<LinkedCreditProductCheck>());

    /// <summary>
    /// A <see cref="CrmProductCatalogue"/> over the <see cref="Administration"/> double. The tenant
    /// id is immaterial to the double — it matches <c>Arg.Any&lt;Guid&gt;()</c> — so the parameter
    /// exists for the tests that read better naming their tenant.
    /// </summary>
    internal static CrmProductCatalogue CrmCatalogue(
        Guid tenantId = default, IAdministrationModule? administration = null)
        => new(
            administration ?? Administration(),
            new FixedTenantContext(tenantId),
            Log<CrmProductCatalogue>());

    /// <summary>
    /// An <see cref="InsurerPricingProbe"/> over a resolver that knows the adapters given. With
    /// none, every connection answers "cannot price" — which is the honest verdict for a
    /// connection whose adapter is not shipped.
    /// </summary>
    internal static InsurerPricingProbe Pricing(
        IntegrationDbContext db, params ICbsAdapter[] adapters)
        => new(Resolver(db, adapters));

    internal static IntegrationAdapterResolver Resolver(
        IntegrationDbContext db, params ICbsAdapter[] adapters)
        => new(db, new KeyedAdapterProvider(adapters.ToDictionary(a => a.Kind.ToString())));

    /// <summary>Seeds one insurance connection, active by default — most tests want a live insurer.</summary>
    /// <param name="at">
    /// When the row was created. Only worth passing when a test seeds SEVERAL connections and the
    /// order matters: the facade's insurance reads iterate <c>OrderBy(c => c.CreatedAt)</c>, and
    /// two rows stamped with the same instant leave "which one is first" to the provider.
    /// </param>
    internal static IntegrationConnection SeedConnection(
        IntegrationDbContext db,
        Guid tenantId,
        IntegrationKind kind = IntegrationKind.Orass,
        string name = "ORASS vie",
        bool active = true,
        IntegrationFamily family = IntegrationFamily.Insurance,
        DateTimeOffset? at = null)
    {
        var clock = Clock(at);

        ConnectionSettings settings = kind switch
        {
            IntegrationKind.Orass => new OrassSettings { IntermediaryCode = "INT-0042" },
            IntegrationKind.Temenos => new TemenosSettings
            {
                BaseUrl = "https://cbs.example.ci/api/",
                AuthMode = TemenosAuthMode.StaticToken,
                CompanyId = "CI0010001",
            },
            _ => new FakeSettings(),
        };

        var connection = IntegrationConnection.Create(
            tenantId: tenantId,
            family: family,
            kind: kind,
            mode: IntegrationMode.Api,
            name: name,
            settings: settings,
            createdBy: Actor,
            clock: clock);

        if (active)
        {
            connection.RecordHealth(
                IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(50), at ?? Now), clock);

            connection.Activate(Actor, clock);
        }

        db.Connections.Add(connection);
        db.SaveChanges();

        return connection;
    }

    /// <summary>
    /// Seeds one catalogue entry. Active by default, unlike the real create path — the tests that
    /// care about the inactive-on-creation rule assert it through the handler.
    /// </summary>
    internal static InsuranceProduct SeedProduct(
        IntegrationDbContext db,
        Guid tenantId,
        Guid connectionId,
        string insurerProductCode = "ASS-VIE-EMP",
        string name = "Assurance emprunteur",
        bool active = true,
        ProductPricingMode pricingMode = ProductPricingMode.CatalogueFixed,
        decimal? fixedPremium = 7_500m,
        string? currency = "XOF",
        DateOnly? effectiveFrom = null,
        DateOnly? effectiveTo = null,
        int? minAge = null,
        int? maxAge = null,
        KycLevel minKycLevel = KycLevel.None,
        bool requiresCbsAccount = false,
        bool requiresActiveLoan = false,
        string? linkedCreditProductCode = null,
        string? crmProductCode = null,
        decimal commissionRate = 0.15m)
    {
        var clock = Clock();

        var product = InsuranceProduct.Create(
            tenantId: tenantId,
            connectionId: connectionId,
            insurerProductCode: insurerProductCode,
            name: name,
            periodicity: PremiumPeriodicity.Annual,
            pricingMode: pricingMode,
            createdBy: Actor,
            clock: clock,
            effectiveFrom: effectiveFrom ?? new DateOnly(2026, 1, 1),
            guaranteesJson: """[{"code":"DC","label":"Décès","ceilingAmount":5000000,"deductible":null}]""",
            fixedPremiumAmount: fixedPremium,
            currency: currency,
            minAge: minAge,
            maxAge: maxAge,
            minKycLevel: minKycLevel,
            requiresCbsAccount: requiresCbsAccount,
            requiresActiveLoan: requiresActiveLoan,
            linkedCreditProductCode: linkedCreditProductCode,
            crmProductCode: crmProductCode,
            commissionRate: commissionRate,
            effectiveTo: effectiveTo);

        if (active) product.Activate(Actor, clock);

        db.InsuranceProducts.Add(product);
        db.SaveChanges();

        return product;
    }

    /// <summary>A plain, valid body. Tests override only the field under test.</summary>
    internal static InsuranceProductWriteRequest Body(
        string name = "Assurance emprunteur",
        ProductPricingMode pricingMode = ProductPricingMode.CatalogueFixed,
        decimal? fixedPremium = 7_500m,
        string? currency = "XOF",
        int? minAge = null,
        int? maxAge = null,
        KycLevel minKycLevel = KycLevel.None,
        bool requiresCbsAccount = false,
        bool requiresActiveLoan = false,
        string? linkedCreditProductCode = null,
        string? crmProductCode = null,
        decimal commissionRate = 0.15m,
        int premiumRetryLimit = 3,
        int premiumRetryIntervalDays = 3,
        DateOnly? effectiveFrom = null,
        DateOnly? effectiveTo = null,
        IReadOnlyList<InsuranceGuarantee>? guarantees = null)
        => new(
            Name: name,
            Periodicity: PremiumPeriodicity.Annual,
            PricingMode: pricingMode,
            EffectiveFrom: effectiveFrom ?? new DateOnly(2026, 1, 1),
            Description: "Couverture décès et invalidité",
            Guarantees: guarantees ?? [new InsuranceGuarantee("DC", "Décès", 5_000_000m, null)],
            FixedPremiumAmount: fixedPremium,
            Currency: currency,
            InsuredAmount: 5_000_000m,
            MinAge: minAge,
            MaxAge: maxAge,
            MinKycLevel: minKycLevel,
            RequiresCbsAccount: requiresCbsAccount,
            RequiresActiveLoan: requiresActiveLoan,
            LinkedCreditProductCode: linkedCreditProductCode,
            CrmProductCode: crmProductCode,
            CommissionRate: commissionRate,
            PremiumRetryLimit: premiumRetryLimit,
            PremiumRetryIntervalDays: premiumRetryIntervalDays,
            EffectiveTo: effectiveTo);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TestCurrentUser(Guid Id, Guid TenantId) : ICurrentUser
    {
        public string DisplayName => "test";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => ["Administrator"];
    }

    private sealed class KeyedAdapterProvider(Dictionary<string, ICbsAdapter> adapters)
        : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => null;

        public object? GetKeyedService(Type serviceType, object? serviceKey)
            => serviceKey is string key && adapters.TryGetValue(key, out var adapter) ? adapter : null;

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => GetKeyedService(serviceType, serviceKey)
               ?? throw new InvalidOperationException($"No adapter keyed {serviceKey}.");
    }
}
