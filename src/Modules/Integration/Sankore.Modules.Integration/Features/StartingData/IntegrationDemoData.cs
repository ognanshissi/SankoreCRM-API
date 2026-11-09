using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.ActivateConnection;
using Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.InformationSheets;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Integration.Features.StartingData;

/// <summary>
/// The demo's insurance (<c>docs/donnees-demarrage-demo.md</c>, lot 3): a simulated insurer — the
/// in-memory adapter, reachable by a test institution only (<see cref="FakeAdapterGate"/>, owner's
/// choice of 2026-11-08) — its products realising the starter's three insurance products, active,
/// each with a demonstration information sheet and its exclusions, so a policy can be subscribed end
/// to end. A demo load of an institution with the Insurance module only; idempotent on the
/// connection's kind, the insurer's product code and the sheet already attached.
/// </summary>
internal sealed class IntegrationDemoData : ITenantStartingData
{
    public const string ConnectionName = "Assureur de démonstration";

    public int Order => 40;
    public string Module => "Assurance de démonstration";

    internal sealed record DemoInsuranceProduct(
        string CrmCode, string InsurerCode, string Name, decimal MonthlyPremium, decimal? InsuredAmount,
        int MinAge, int MaxAge, IReadOnlyList<InsuranceGuarantee> Guarantees, string Exclusions);

    internal static readonly IReadOnlyList<DemoInsuranceProduct> Products =
    [
        new("ASS-SANTE", "DEMO-SANTE", "Santé Famille", 15_000m, 2_000_000m, 0, 65,
            [new("HOSP", "Hospitalisation", 1_500_000m, 0m), new("SOINS", "Soins courants et pharmacie", 300_000m, 2_000m)],
            "Maladies antérieures à l'adhésion non déclarées\nSoins esthétiques\nCures thermales"),
        new("ASS-PREVOYANCE", "DEMO-PREVOYANCE", "Prévoyance Sérénité", 3_500m, 1_000_000m, 18, 65,
            [new("DECES", "Capital décès", 1_000_000m, null), new("OBSEQUES", "Frais d'obsèques", 250_000m, null)],
            "Suicide la première année\nFaits de guerre"),
        new("ASS-EMPRUNTEUR", "DEMO-EMPRUNTEUR", "Assurance Emprunteur", 2_000m, null, 18, 70,
            [new("DECES-INV", "Décès et invalidité absolue", null, null)],
            "Sports à risque non déclarés\nMaladies antérieures au prêt non déclarées"),
    ];

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo
            || !await services.GetRequiredService<ITenantModules>().IsEnabledAsync(TenantModules.Insurance, ct))
            return created;

        var db = services.GetRequiredService<IntegrationDbContext>();
        var sender = services.GetRequiredService<ISender>();
        var administration = services.GetRequiredService<IAdministrationModule>();
        var files = services.GetRequiredService<IFileStore>();
        var clock = services.GetRequiredService<TimeProvider>();

        // The simulated insurer, healthy then active (activation requires a passed check).
        var connection = await db.Connections.AsTracking()
            .FirstOrDefaultAsync(c => c.Family == IntegrationFamily.Insurance && c.Kind == IntegrationKind.Fake, ct);
        var connectionId = connection?.Id ?? Ok(await sender.Send(new CreateConnectionCommand(
            IntegrationFamily.Insurance, IntegrationKind.Fake, IntegrationMode.Api, ConnectionName,
            new FakeSettings { Family = IntegrationFamily.Insurance }), ct), ConnectionName);
        if (connection is null) created["Assureur simulé"] = 1;
        if (connection is not { IsActive: true })
        {
            Ok(await sender.Send(new CheckConnectionHealthCommand(connectionId), ct), ConnectionName);
            Ok(await sender.Send(new ActivateConnectionCommand(connectionId), ct), ConnectionName);
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        int products = 0, sheets = 0;
        foreach (var demo in Products)
        {
            // Realises the starter's catalogue entry; an institution that deleted it gets no insurer product.
            if (await administration.GetProductAsync(request.TenantId, demo.CrmCode, ct) is null) continue;

            var product = await db.InsuranceProducts.AsTracking()
                .FirstOrDefaultAsync(p => p.ConnectionId == connectionId && p.InsurerProductCode == demo.InsurerCode, ct);
            if (product is null)
            {
                var dto = Ok(await sender.Send(new CreateInsuranceProductCommand(connectionId, demo.InsurerCode,
                    new InsuranceProductWriteRequest(
                        demo.Name, PremiumPeriodicity.Monthly, ProductPricingMode.CatalogueFixed, today,
                        Description: "Produit de démonstration de l'assureur simulé.",
                        Guarantees: demo.Guarantees, FixedPremiumAmount: demo.MonthlyPremium, Currency: "XOF",
                        InsuredAmount: demo.InsuredAmount, MinAge: demo.MinAge, MaxAge: demo.MaxAge,
                        CrmProductCode: demo.CrmCode, CommissionRate: 0.15m)), ct), demo.InsurerCode);
                product = await db.InsuranceProducts.AsTracking().FirstAsync(p => p.Id == dto.Id, ct);
                products++;
            }
            if (!product.IsActive)
                Ok(await sender.Send(new ActivateInsuranceProductCommand(product.Id), ct), demo.InsurerCode);

            if (product.CurrentInformationSheetId is null)
            {
                // The commands above ran in scopes of their own: read the row as they left it, or
                // this save would fight their concurrency token.
                db.ChangeTracker.Clear();
                product = await db.InsuranceProducts.AsTracking().FirstAsync(p => p.Id == product.Id, ct);
                await using var pdf = new MemoryStream(Sheet(demo));
                await InformationSheetEndpoints.AttachAsync(db, files, product, pdf,
                    $"fiche-information-{demo.InsurerCode.ToLowerInvariant()}.pdf", request.TenantId, request.SystemUserId, clock, ct);
                product.SetExclusions(demo.Exclusions, request.SystemUserId, clock);
                await db.SaveChangesAsync(ct);
                sheets++;
            }
        }
        if (products > 0) created["Produits assureur"] = products;
        if (sheets > 0) created["Fiches d'information"] = sheets;
        return created;
    }

    internal static byte[] Sheet(DemoInsuranceProduct demo)
    {
        var lines = new List<string>
        {
            "DOCUMENT DE DÉMONSTRATION - sans valeur contractuelle",
            "",
            $"Assureur : {ConnectionName}",
            $"Prime mensuelle : {demo.MonthlyPremium:N0} FCFA",
            $"Âge à l'adhésion : de {demo.MinAge} à {demo.MaxAge} ans",
            demo.InsuredAmount is { } insured ? $"Capital ou plafond assuré : {insured:N0} FCFA" : "Capital assuré : montant restant dû du prêt",
            "",
            "Garanties :",
        };
        lines.AddRange(demo.Guarantees.Select(g => g.CeilingAmount is { } ceiling
            ? $"  - {g.Label} : jusqu'à {ceiling:N0} FCFA"
            : $"  - {g.Label}"));
        lines.Add("");
        lines.Add("Exclusions :");
        lines.AddRange(demo.Exclusions.Split('\n').Select(e => $"  - {e}"));
        return DemoPdf.Page($"Fiche d'information - {demo.Name}", lines);
    }

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");

    private static void Ok(Result result, string what)
    {
        if (result.IsFailure) throw new InvalidOperationException($"{what} : {result.Error}");
    }
}
