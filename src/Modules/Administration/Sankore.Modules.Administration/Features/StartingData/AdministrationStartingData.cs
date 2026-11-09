using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Features.Products.CreateProduct;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.StartingData;

/// <summary>
/// The « Démarrage » share of Administration (<c>docs/donnees-demarrage-demo.md</c>, owner's choices
/// of 2026-11-08): two agencies to rename and a catalogue of usual products, ACTIVE, for the paid
/// modules the institution has. Sent through the ordinary commands; idempotent on the product code,
/// and the agencies are created only for an institution that has none — one that laid out its own
/// network must not find a « Siège » it never asked for.
/// </summary>
internal sealed class AdministrationStartingData : ITenantStartingData
{
    public const string HeadQuarter = "Siège";
    public const string MainBranch = "Agence principale";

    public int Order => 10;
    public string Module => "Agences et produits";

    /// <summary>The starter catalogue: code, name, family, what it is.</summary>
    internal static readonly IReadOnlyList<(string Code, string Name, ProductCategory Category, string Description)> Catalogue =
    [
        ("CPT-COURANT", "Compte courant", ProductCategory.CurrentAccount, "Compte de dépôt à vue pour les opérations courantes."),
        ("CPT-EPARGNE", "Compte d'épargne", ProductCategory.Savings, "Épargne rémunérée, versements libres."),
        ("TONTINE", "Tontine", ProductCategory.Tontine, "Épargne collective à tour de rôle."),
        ("CREDIT-INDIV", "Crédit individuel", ProductCategory.Loan, "Crédit d'investissement ou de fonds de roulement pour une activité."),
        ("CREDIT-GROUPE", "Crédit de groupe", ProductCategory.GroupCredit, "Crédit solidaire accordé à un groupement."),
        ("CREDIT-AGRI", "Crédit agricole de campagne", ProductCategory.Agriculture, "Financement d'une campagne agricole, remboursé à la récolte."),
        ("ASS-SANTE", "Assurance santé", ProductCategory.HealthInsurance, "Prise en charge des frais de santé du foyer."),
        ("ASS-PREVOYANCE", "Prévoyance décès", ProductCategory.ForecastInsurance, "Capital versé aux bénéficiaires en cas de décès."),
        ("ASS-EMPRUNTEUR", "Assurance emprunteur", ProductCategory.Insurance, "Couverture du crédit en cas de décès ou d'invalidité de l'emprunteur."),
    ];

    /// <summary>
    /// The catalogue of an institution with NO paid module (docs/isolation-socle.md, lot 5): Standard
    /// products of a business that sells and follows up — to rename, the way the agencies are.
    /// </summary>
    internal static readonly IReadOnlyList<(string Code, string Name, ProductCategory Category, string Description)> SocleCatalogue =
    [
        ("OFFRE-ESSENTIEL", "Offre Essentiel", ProductCategory.Standard, "L'offre d'entrée de gamme, pour un premier achat."),
        ("OFFRE-PREMIUM", "Offre Premium", ProductCategory.Standard, "L'offre complète, avec un accompagnement personnalisé."),
        ("INSTALLATION", "Installation et mise en service", ProductCategory.Standard, "La prestation d'installation chez le client."),
        ("CONTRAT-MAINTENANCE", "Contrat de maintenance", ProductCategory.Standard, "Le suivi et l'entretien, à l'année."),
    ];

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AdministrationDbContext>();
        var sender = services.GetRequiredService<ISender>();
        var modules = services.GetRequiredService<ITenantModules>();
        var created = new Dictionary<string, int>();

        if (!await db.Agencies.AnyAsync(ct))
        {
            var hq = Ok(await sender.Send(new CreateAgencyCommand(HeadQuarter, "Direction générale.", AgencyType.HeadQuarter,
                null, null, "Abidjan", null, "Côte d'Ivoire", null, null, null), ct), HeadQuarter);
            Ok(await sender.Send(new CreateAgencyCommand(MainBranch, "Première agence du réseau.", AgencyType.Branch,
                hq.AgencyId, null, "Abidjan", null, "Côte d'Ivoire", null, null, null), ct), MainBranch);
            created["Agences"] = 2;
        }

        var codes = (await db.ProductSpecialities.Select(p => p.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var products = 0;
        var socle = !await modules.IsEnabledAsync(TenantModules.BankingMicrofinance, ct) && !await modules.IsEnabledAsync(TenantModules.Insurance, ct);
        foreach (var (code, name, category, description) in socle ? SocleCatalogue : Catalogue)
        {
            // Only the families of the modules the institution pays for; the command would refuse the others.
            if (codes.Contains(code) || !await modules.AllowsCategoryAsync(category, ct)) continue;
            Ok(await sender.Send(new CreateProductCommand(name, code, category, description), ct), code);
            products++;
        }
        if (products > 0) created["Produits"] = products;

        return created;
    }

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");
}
