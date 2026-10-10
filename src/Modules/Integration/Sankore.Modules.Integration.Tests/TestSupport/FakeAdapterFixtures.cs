namespace Sankore.Modules.Integration.Tests.TestSupport;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Payloads and hosts the fake-adapter suites feed in, in one place.
///
/// <para>
/// They are shared because the contract suite asserts on OUTCOMES and never on the payload: a
/// test that builds its own twenty-field <see cref="CbsCustomerPayload"/> inline buries the one
/// line that matters. Every value here is fixed — the doubles are deterministic, and a
/// <c>Guid.NewGuid()</c> in a fixture would be the one thing that varies between two runs.
/// </para>
/// </summary>
internal static class FakeAdapterFixtures
{
    /// <summary>The CRM customer every fixture speaks about, unless told otherwise.</summary>
    public static readonly Guid CrmCustomerId = new("22222222-2222-2222-2222-222222222222");

    public static readonly Guid CrmProductId = new("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// A complete Ivorian individual — the commonest case in production, and the one whose code
    /// fields (id document type, gender, profession) an adapter has to translate.
    /// </summary>
    public static CbsCustomerPayload CustomerPayload(Guid? crmCustomerId = null) => new(
        CrmCustomerId: crmCustomerId ?? CrmCustomerId,
        FirstName: "AWA",
        LastName: "OUATTARA",
        LegalName: null,
        DateOfBirth: new DateOnly(1987, 4, 2),
        Gender: "F",
        MaritalStatus: "Married",
        Nationality: "CIV",
        IdDocumentType: "CNI",
        IdDocumentNumber: "CI0012345678",
        PhoneNumber: "+2250707070707",
        Email: "awa.ouattara@example.ci",
        AddressLine: "Rue des Jardins, Cocody",
        City: "Abidjan",
        Country: "CI",
        Profession: "Commerçante",
        Sector: "Commerce",
        AgencyCode: "AG-ABJ-01",
        KycLevel: KycLevel.Simplified,
        CrmReference: "CRM-000123");

    public static CbsLoanApplicationPayload LoanApplication(
        ExternalId customerId, string productCode) => new(
        CrmCustomerId: CrmCustomerId,
        CustomerId: customerId,
        ProductCode: productCode,
        Amount: 500_000m,
        Currency: FakeAdapter.SeededCurrency,
        TermMonths: 24,
        Purpose: "Fonds de roulement");

    public static InsurancePolicyPayload PolicyPayload(string insurerProductCode) => new(
        CrmCustomerId: CrmCustomerId,
        CrmProductId: CrmProductId,
        InsurerProductCode: insurerProductCode,
        EffectiveDate: new DateOnly(2024, 2, 1),
        PremiumAmount: 7_500m,
        Currency: FakeAdapter.SeededCurrency,
        Periodicity: PremiumPeriodicity.Monthly,
        Beneficiaries:
        [
            new InsuranceBeneficiary("KONE MAMADOU", "Époux", 100m, new DateOnly(1983, 9, 14)),
        ],
        LinkedLoanReference: null,
        ConsentEvidenceRef: "consent/2024/000123");

    public static InsuranceClaimPayload ClaimPayload(ExternalId policyId) => new(
        PolicyId: policyId,
        OccurredOn: new DateOnly(2024, 1, 20),
        Nature: "Décès",
        Description: "Décès de l'assuré, acte transmis par l'agence.",
        DocumentStorageRefs: ["kyc/2024/acte-deces.pdf"]);

    /// <summary>
    /// A persisted connection, for <c>CheckHealthAsync</c>. Carries <see cref="FakeSettings"/>
    /// because that is the only settings record the domain will accept on a Fake row.
    /// </summary>
    public static IntegrationConnection Connection(FakeSettings? settings = null)
        => IntegrationConnection.Create(
            tenantId: new Guid("44444444-4444-4444-4444-444444444444"),
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "Double local",
            settings: settings ?? new FakeSettings(),
            createdBy: new Guid("55555555-5555-5555-5555-555555555555"),
            clock: TimeProvider.System);
}

/// <summary>
/// The smallest <see cref="IHostEnvironment"/> that answers a name.
///
/// <para>
/// Hand-written rather than taken from <c>Microsoft.Extensions.Hosting</c>: the environment is
/// the whole gate <c>AddFakeAdapter</c> is built on, and asserting the gate needs a host that
/// claims to be Production — which no helper in the framework hands out.
/// </para>
/// </summary>
internal sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
{
    public static StubHostEnvironment Development => new(Environments.Development);

    public static StubHostEnvironment Production => new(Environments.Production);

    public static StubHostEnvironment Staging => new(Environments.Staging);

    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "Sankore.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } =
        new NullFileProvider();
}
