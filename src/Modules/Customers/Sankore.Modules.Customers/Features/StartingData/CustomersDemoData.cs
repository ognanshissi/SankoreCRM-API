using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Complaints;
using Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.StartingData;

/// <summary>
/// The demo's clients (<c>docs/donnees-demarrage-demo.md</c>, lot 4): fifteen individuals and five
/// companies in the demo's two branches, each with its advisor, and three complaints at different
/// stages. Through the ordinary commands, so every client gets its KYC file as on screen. Fictitious
/// identities: document numbers carry « DEMO », addresses are <c>example.com</c> (RFC 2606) and the
/// phone numbers belong to no team member, so the test-mode filter keeps every message in.
/// Idempotent on the identity document and the registration number — a client already there is
/// reported by the command as a duplicate, and its id is reused.
/// </summary>
internal sealed class CustomersDemoData : ITenantStartingData
{
    public int Order => 50;
    public string Module => "Clients de démo";

    internal sealed record DemoPerson(
        string Key, string FirstName, string LastName, Gender Gender, DateOnly BirthDate, string Profession,
        decimal Income, string Branch, string Advisor, string District);

    internal sealed record DemoCompany(
        string Key, string Name, string LegalForm, DateOnly Incorporated, string Branch, string Advisor, string District);

    private const string Plateau = "Agence Plateau";
    private const string Yopougon = "Agence Yopougon";

    internal static readonly IReadOnlyList<DemoPerson> People =
    [
        new("awa-kone", "Awa", "Koné", Gender.Female, new(1988, 3, 14), "Commerçante", 450_000m, Plateau, "gestionnaire-comptes", "Plateau"),
        new("kouame-nguessan", "Kouamé", "N'Guessan", Gender.Male, new(1979, 7, 2), "Enseignant", 380_000m, Plateau, "agent-plateau-1", "Cocody"),
        new("aya-yao", "Aya", "Yao", Gender.Female, new(1992, 11, 21), "Couturière", 220_000m, Plateau, "agent-plateau-1", "Treichville"),
        new("seydou-traore", "Seydou", "Traoré", Gender.Male, new(1985, 1, 9), "Transporteur", 600_000m, Plateau, "agent-plateau-2", "Adjamé"),
        new("adjoua-kouassi", "Adjoua", "Kouassi", Gender.Female, new(1990, 5, 30), "Restauratrice", 350_000m, Plateau, "agent-plateau-2", "Marcory"),
        new("bakary-diarra", "Bakary", "Diarra", Gender.Male, new(1976, 9, 17), "Agriculteur", 280_000m, Plateau, "gestionnaire-credit", "Anyama"),
        new("mariame-sylla", "Mariame", "Sylla", Gender.Female, new(1995, 2, 4), "Coiffeuse", 180_000m, Plateau, "agent-commercial-plateau", "Koumassi"),
        new("eric-gbagbo-kra", "Éric", "Kra", Gender.Male, new(1982, 12, 12), "Mécanicien", 300_000m, Plateau, "gestionnaire-comptes", "Port-Bouët"),
        new("fanta-camara", "Fanta", "Camara", Gender.Female, new(1987, 6, 25), "Pharmacienne", 900_000m, Yopougon, "agent-yopougon-1", "Yopougon Siporex"),
        new("ibrahima-ouedraogo", "Ibrahima", "Ouédraogo", Gender.Male, new(1983, 4, 8), "Boulanger", 420_000m, Yopougon, "agent-yopougon-1", "Yopougon Niangon"),
        new("grace-akissi", "Grâce", "Akissi", Gender.Female, new(1998, 8, 19), "Étudiante", 60_000m, Yopougon, "agent-yopougon-2", "Yopougon Toits Rouges"),
        new("koffi-ahoua", "Koffi", "Ahoua", Gender.Male, new(1970, 10, 3), "Planteur de cacao", 520_000m, Yopougon, "agent-yopougon-2", "Songon"),
        new("salimata-doumbia", "Salimata", "Doumbia", Gender.Female, new(1989, 1, 27), "Vendeuse de pagnes", 260_000m, Yopougon, "agent-commercial-yopougon", "Yopougon Selmer"),
        new("jean-baptiste-ake", "Jean-Baptiste", "Aké", Gender.Male, new(1978, 3, 5), "Électricien", 340_000m, Yopougon, "agent-yopougon-1", "Abobo"),
        new("nathalie-bleu", "Nathalie", "Bléou", Gender.Female, new(1993, 7, 11), "Infirmière", 480_000m, Yopougon, "agent-yopougon-2", "Yopougon Maroc"),
    ];

    internal static readonly IReadOnlyList<DemoCompany> Companies =
    [
        new("coop-cacao-songon", "Coopérative Cacao de Songon", "SCOOPS", new(2014, 5, 12), Yopougon, "gestionnaire-credit", "Songon"),
        new("boulangerie-espoir", "Boulangerie de l'Espoir", "SARL", new(2017, 2, 1), Yopougon, "agent-yopougon-1", "Yopougon Niangon"),
        new("transports-abidjan-express", "Abidjan Express Transports", "SA", new(2011, 9, 20), Plateau, "gestionnaire-comptes", "Plateau"),
        new("gie-femmes-marcory", "GIE des Femmes de Marcory", "GIE", new(2019, 3, 8), Plateau, "agent-plateau-2", "Marcory"),
        new("ong-sante-pour-tous", "ONG Santé pour Tous", "ONG", new(2016, 11, 30), Plateau, "gestionnaire-comptes", "Cocody"),
    ];

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo) return created;

        var sender = services.GetRequiredService<ISender>();
        Guid Agency(string name) => request.Shared.TryGetValue(DemoKeys.Agency(name), out var id) ? id
            : throw new InvalidOperationException($"Agence « {name} » introuvable : les comptes de démo doivent être chargés avant les clients.");
        // An account or credit manager is Banking - Microfinance's role: without it, the branch's first
        // agent follows the client (docs/isolation-socle.md, lot 5).
        Guid? Advisor(string profile, string branch)
            => request.Shared.TryGetValue(DemoKeys.Account(profile), out var id) ? id
             : request.Shared.TryGetValue(DemoKeys.Account(branch == Yopougon ? "agent-yopougon-1" : "agent-plateau-1"), out var agent) ? agent
             : null;

        var clients = 0;
        for (var i = 0; i < People.Count; i++)
        {
            var p = People[i];
            var result = Ok(await sender.Send(new CreateIndividualClientCommand(
                Agency(p.Branch), Advisor(p.Advisor, p.Branch), p.FirstName, p.LastName, null, p.Gender, p.BirthDate, "Abidjan",
                "Ivoirienne", MaritalStatus.Married, null, null, p.Profession, null, p.Income, "XOF", "fr",
                IdentityDocumentType.NationalIdCard, $"CI-DEMO-{i + 1:D6}", new DateOnly(2021, 1, 15), new DateOnly(2031, 1, 14),
                [Phone(i + 1)], $"{p.Key}@example.com", new PostalAddressInput(p.District, "Abidjan", null, "CI", null),
                ConfirmNoDuplicate: true), ct), p.Key);
            var clientId = result.Outcome == CreateClientOutcome.BlockedDuplicateIdentityDocument
                ? result.Candidates.First().ClientId
                : result.ClientId!.Value;
            if (result.Outcome != CreateClientOutcome.BlockedDuplicateIdentityDocument) clients++;
            request.Shared[DemoKeys.Client(p.Key)] = clientId;
        }

        for (var i = 0; i < Companies.Count; i++)
        {
            var c = Companies[i];
            var result = Ok(await sender.Send(new CreateLegalClientCommand(
                Agency(c.Branch), Advisor(c.Advisor, c.Branch), c.Name, c.LegalForm, $"CI-ABJ-DEMO-{c.Incorporated.Year}-B-{i + 1:D5}", null,
                c.Incorporated, new PostalAddressInput(c.District, "Abidjan", null, "CI", null),
                [Phone(50 + i)], $"{c.Key}@example.com", "fr"), ct), c.Key);
            var clientId = result.Outcome == CreateLegalClientOutcome.BlockedDuplicateRegistrationNumber
                ? result.Candidates.First().ClientId
                : result.ClientId!.Value;
            if (result.Outcome != CreateLegalClientOutcome.BlockedDuplicateRegistrationNumber) clients++;
            request.Shared[DemoKeys.Client(c.Key)] = clientId;
        }
        if (clients > 0) created["Clients"] = clients;

        // Three complaints: one just received, one acknowledged, one answered.
        var db = services.GetRequiredService<CustomersDbContext>();
        var complaints = 0;
        // A banking category needs Banking - Microfinance (docs/isolation-socle.md, lot 4).
        var banking = await services.GetRequiredService<ITenantModules>().IsEnabledAsync(request.TenantId, TenantModules.BankingMicrofinance, ct);
        foreach (var complaint in Complaints)
        {
            // A credit complaint becomes a delivery one in an institution without banking (lot 5).
            var (key, channel, category, subject, description, stage) =
                !banking && ClientComplaintFiling.IsBankingCategory(complaint.Category) ? SocleComplaint : complaint;
            var clientId = request.Shared[DemoKeys.Client(key)];
            if (await db.ClientComplaints.AnyAsync(c => c.ClientId == clientId, ct)) continue;
            var filed = Ok(await sender.Send(new FileClientComplaintCommand(clientId, channel, category, subject, description, null), ct), subject);
            if (stage >= 1) Ok(await sender.Send(new AcknowledgeClientComplaintCommand(clientId, filed.ComplaintId), ct), subject);
            if (stage >= 2) Ok(await sender.Send(new ResolveClientComplaintCommand(clientId, filed.ComplaintId,
                "Les frais ont été recalculés et le trop-perçu remboursé au client."), ct), subject);
            complaints++;
        }
        if (complaints > 0) created["Réclamations"] = complaints;

        return created;
    }

    internal static readonly IReadOnlyList<(string Client, ClientComplaintChannel Channel, ClientComplaintCategory Category, string Subject, string Description, int Stage)> Complaints =
    [
        ("seydou-traore", ClientComplaintChannel.Branch, ClientComplaintCategory.FeesAndPricing, "Frais de dossier facturés deux fois",
            "Le client conteste des frais de dossier facturés deux fois ce mois-ci.", 2),
        ("fanta-camara", ClientComplaintChannel.Telephone, ClientComplaintCategory.Credit, "Retard de déblocage du crédit",
            "La cliente attend le déblocage de son crédit depuis dix jours et demande des explications.", 1),
        ("koffi-ahoua", ClientComplaintChannel.WhatsApp, ClientComplaintCategory.Service, "Accueil en agence",
            "Le client signale une longue attente au guichet de l'agence Yopougon.", 0),
    ];

    internal static readonly (string Client, ClientComplaintChannel Channel, ClientComplaintCategory Category, string Subject, string Description, int Stage) SocleComplaint =
        ("fanta-camara", ClientComplaintChannel.Telephone, ClientComplaintCategory.Service, "Retard de livraison de la commande",
            "La cliente attend la livraison de sa commande depuis dix jours et demande des explications.", 1);

    /// <summary>A Côte d'Ivoire mobile number reserved for the demo: +225 07 99 00 0x xx.</summary>
    internal static string Phone(int n) => $"+22507990{n:D5}";

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");

    private static void Ok(Result result, string what)
    {
        if (result.IsFailure) throw new InvalidOperationException($"{what} : {result.Error}");
    }
}
