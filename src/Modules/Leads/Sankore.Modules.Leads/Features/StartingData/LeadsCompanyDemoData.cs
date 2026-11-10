namespace Sankore.Modules.Leads.Features.StartingData;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.DispatchingRules;
using Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.Quote;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Settings.KeyAccounts;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// The key-account prospecting of a socle demo (docs/prospection-grands-comptes.md, lot 5): the
/// thresholds, a « Formulaire de devis » source tied to the Standard premium offer, a key-account
/// dispatching rule for the two sales agents, and five company prospects from a corner shop to a
/// wholesaler — one of them a key account that asked for a product through the form, so it reads
/// « Chaud » and « Grand compte » at once.
///
/// <para>
/// Socle institutions only: a module's demo has its own products, and its company prospects would
/// ask for a credit. Idempotent on natural keys — the « DEMO-E… » reference of a prospect, the
/// source code, the rule name — and the thresholds are only set when the institution has none, so a
/// reload never overwrites what a presenter tuned.
/// </para>
/// </summary>
internal sealed class LeadsCompanyDemoData : ITenantStartingData
{
    public int Order => 65;
    public string Module => "Grands comptes de démo";

    internal const string QuoteFormCode = "FORMULAIRE-DEVIS";
    internal const string QuoteFormProduct = "OFFRE-PREMIUM";
    internal const string KeyAccountRuleName = "Grands comptes";

    internal const decimal GoodPurchases = 10_000_000m;
    internal const decimal MajorPurchases = 50_000_000m;
    internal static readonly string[] TargetSectors = ["WholesaleTrade", "Construction", "HospitalityTourism"];

    /// <summary>The sales agents who receive the key accounts.</summary>
    internal static readonly string[] KeyAccountAgents = ["agent-commercial-plateau", "agent-commercial-yopougon"];

    internal sealed record Company(
        string Reference, string Name, string ContactFirst, string ContactLast, string JobTitle,
        string Sector, LeadCompanySize Size, decimal Purchases, string SourceCode, string Product,
        string Owner, string Branch, bool WithProject);

    /// <summary>From a key account asking through the form down to a corner shop.</summary>
    internal static readonly IReadOnlyList<Company> Companies =
    [
        new("DEMO-E001", "Ivoire Distribution SA", "Serge", "Kouamé", "Responsable des achats",
            "WholesaleTrade", LeadCompanySize.Large, 85_000_000m, QuoteFormCode, "Offre Premium",
            "agent-commercial-plateau", "Agence Plateau", WithProject: false),
        new("DEMO-E002", "Clinique Sainte-Anne", "Mireille", "Assi", "Directrice administrative",
            "Health", LeadCompanySize.Medium, 60_000_000m, "PARRAINAGE", "Contrat de maintenance",
            "agent-commercial-yopougon", "Agence Yopougon", WithProject: false),
        new("DEMO-E003", "BTP Lagune Construction", "Ibrahim", "Sanogo", "Directeur technique",
            "Construction", LeadCompanySize.Medium, 30_000_000m, "AGENCE", "Installation et mise en service",
            "agent-commercial-plateau", "Agence Plateau", WithProject: true),
        new("DEMO-E004", "Hôtel Les Palmiers", "Rose", "Ehui", "Économe",
            "HospitalityTourism", LeadCompanySize.Small, 12_000_000m, QuoteFormCode, "Contrat de maintenance",
            "agent-commercial-yopougon", "Agence Yopougon", WithProject: false),
        new("DEMO-E005", "Boutique Chez Mariam", "Mariam", "Fofana", "Gérante",
            "RetailTrade", LeadCompanySize.Micro, 2_000_000m, "AGENCE", "Offre Essentiel",
            "agent-plateau-1", "Agence Plateau", WithProject: false),
    ];

    /// <summary>Numbers of the demo's company contacts, beyond the 48 prospects: +225 07 98 00 1xx.</summary>
    internal static string Phone(int n) => LeadsDemoData.Phone(100 + n);

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo) return created;

        var modules = services.GetRequiredService<ITenantModules>();
        if (await modules.IsEnabledAsync(request.TenantId, TenantModules.BankingMicrofinance, ct)
            || await modules.IsEnabledAsync(request.TenantId, TenantModules.Insurance, ct))
            return created;

        var db = services.GetRequiredService<LeadsDbContext>();
        var sender = services.GetRequiredService<ISender>();
        Guid Account(string profile) => request.Shared.TryGetValue(DemoKeys.Account(profile), out var id) ? id
            : throw new InvalidOperationException($"Compte « {profile} » introuvable : les comptes de démo doivent être chargés avant l'activité.");
        Guid Agency(string name) => request.Shared[DemoKeys.Agency(name)];

        // ── The thresholds, before the prospects: their score reads them ─────
        if (!(await KeyAccountSettings.ReadAsync(db, request.TenantId, ct)).IsConfigured)
        {
            Ok(await sender.Send(new SetKeyAccountSettingsCommand(
                request.TenantId, GoodPurchases, MajorPurchases, TargetSectors, Guid.Empty), ct), "Grands comptes");
            created["Réglage grands comptes"] = 1;
        }

        // ── The quote form: a web form tied to the premium offer ──────────────
        if (!await db.LeadSourceConfigs.AnyAsync(s => s.Code == QuoteFormCode, ct))
        {
            var order = await db.LeadSourceConfigs.CountAsync(ct);
            Ok(await sender.Send(new CreateLeadSourceCommand(
                request.TenantId, QuoteFormCode, "Formulaire de devis", LeadChannelType.WebForm, order + 1,
                IntegrationMode.EmbeddedScript,
                "Demande de devis du site : le produit demandé rend le lead chaud. À configurer : adresse du site autorisée, puis activation.",
                new EmbeddedScriptSettings { DefaultProductCode = QuoteFormProduct }), ct), QuoteFormCode);
            created["Sources de lead"] = 1;
        }

        // ── The key-account rule, for the sales agents only ──────────────────
        if (!await db.DispatchingRules.AnyAsync(r => r.Name == KeyAccountRuleName, ct))
        {
            Ok(await sender.Send(new CreateDispatchingRuleCommand(
                request.TenantId, KeyAccountRuleName, DispatchingStrategy.RoundRobin,
                new ScoringWeightsDto(25, 25, 20, 15, 15), MaxLeadsPerAgent: 30, MaxTasksPerAgent: 0,
                AntiMonopolyThreshold: 5, FirstContactSla: TimeSpan.FromHours(4), Priority: 0,
                KeyAccountsOnly: true, ReservedAgentIds: [.. KeyAccountAgents.Select(Account)]), ct), KeyAccountRuleName);
            created["Règles de dispatching"] = 1;
        }

        // ── The company prospects ─────────────────────────────────────────────
        var sourceIds = await db.LeadSourceConfigs.ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var existing = (await db.Leads.Where(l => l.ExternalReference != null && l.ExternalReference.StartsWith("DEMO-E"))
            .Select(l => l.ExternalReference!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        int leads = 0, projects = 0;
        for (var n = 0; n < Companies.Count; n++)
        {
            var c = Companies[n];
            if (existing.Contains(c.Reference)) continue;

            var owner = Account(c.Owner);
            var captured = Ok(await sender.Send(new CaptureLeadCommand(
                request.TenantId, $"{c.ContactFirst} {c.ContactLast}", Phone(n + 1),
                c.SourceCode == "PARRAINAGE" ? LeadSource.Referral : c.SourceCode == QuoteFormCode ? LeadSource.Web : LeadSource.Agency,
                c.Product, "fr", 5.3364, -4.0267, Agency(c.Branch),
                FirstName: c.ContactFirst, LastName: c.ContactLast,
                Email: $"contact{n + 1}@{Domain(c.Name)}.example.com",
                Comment: "Prospect entreprise de démonstration.", ExternalReference: c.Reference,
                OwnerId: owner, AgencyId: Agency(c.Branch), Force: true,
                ProspectType: LeadType.Corporate, CompanyName: c.Name, Website: $"https://{Domain(c.Name)}.example.com",
                ActivitySector: c.Sector, CompanySize: c.Size.ToString(), EstimatedAnnualPurchases: c.Purchases,
                ContactJobTitle: c.JobTitle, LeadSourceConfigId: sourceIds.GetValueOrDefault(c.SourceCode)), ct), c.Reference);
            leads++;

            if (!c.WithProject) continue;
            var leadId = captured.LeadId!.Value;
            using (BackgroundJobContext.SetScope(request.TenantId, owner, "Démo"))
                Ok(await sender.Send(new CreateOpportunityCommand(request.TenantId, leadId,
                    $"{c.Product} — {c.Name}", c.Product, owner, "Équipement de trois chantiers.", c.Purchases / 3, "XOF",
                    now.AddDays(25)), ct), c.Reference);
            projects++;
        }
        if (leads > 0) created["Prospects entreprise"] = leads;
        if (projects > 0) created["Projets"] = projects;

        await LoadOffersAsync(request, services, db, sender, Account, created, ct);
        return created;
    }

    /// <summary>A demo offer: whose prospect, the project to open if it has none, the lines, and whether to print the quote.</summary>
    internal sealed record DemoOffer(
        string Reference, string ProjectTitle, string Product, string Need,
        IReadOnlyList<(string Code, decimal Quantity, decimal DiscountPercent)> Lines, bool Issue);

    /// <summary>
    /// The quotes of the socle demo (devis en lignes, lot 6): BTP Lagune's three sites priced and
    /// printed (DEV-…-000001), and Ivoire Distribution's volume offer at 15 % — beyond the demo's 10 %
    /// ceiling, so it waits for the Plateau branch manager. Prices come from the catalogue, set by
    /// <c>AdministrationDemoData</c> (Order 30) before this step.
    /// </summary>
    internal static readonly IReadOnlyList<DemoOffer> Offers =
    [
        new("DEMO-E003", "Installation et mise en service — BTP Lagune Construction", "Installation et mise en service",
            "Équipement de trois chantiers.",
            [("INSTALLATION", 3, 0), ("OFFRE-PREMIUM", 3, 0), ("CONTRAT-MAINTENANCE", 1, 5)], Issue: true),
        new("DEMO-E001", "Offre Premium — Ivoire Distribution SA", "Offre Premium",
            "Équipement des vingt points de vente du réseau.",
            [("OFFRE-PREMIUM", 20, 15), ("INSTALLATION", 20, 0)], Issue: false),
    ];

    /// <summary>
    /// Idempotent on its own: an offer is only priced on a project that has none, so loading the
    /// demo again over an older one adds the quotes without touching what a presenter changed.
    /// A missing attachment store only skips the PDF — the offer stays.
    /// </summary>
    private static async Task LoadOffersAsync(
        StartingDataRequest request, IServiceProvider services, LeadsDbContext db, ISender sender,
        Func<string, Guid> account, Dictionary<string, int> created, CancellationToken ct)
    {
        var administration = services.GetRequiredService<IAdministrationModule>();
        var now = DateTimeOffset.UtcNow;
        int offers = 0, quotes = 0;
        foreach (var offer in Offers)
        {
            var company = Companies.First(c => c.Reference == offer.Reference);
            var lead = await db.Leads.Where(l => l.ExternalReference == offer.Reference)
                .Select(l => new { l.Id, l.OwnerId }).FirstOrDefaultAsync(ct);
            if (lead is null) continue;
            var owner = lead.OwnerId ?? account(company.Owner);

            var project = await db.Opportunities
                .Where(o => o.LeadId == lead.Id && o.Stage != OpportunityStage.ClosedLost && o.Stage != OpportunityStage.ClosedWon)
                .OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstOrDefaultAsync(ct);
            if (project == Guid.Empty)
                using (BackgroundJobContext.SetScope(request.TenantId, owner, "Démo"))
                    project = Ok(await sender.Send(new CreateOpportunityCommand(request.TenantId, lead.Id,
                        offer.ProjectTitle, offer.Product, owner, offer.Need, null, null, now.AddDays(30)), ct), offer.Reference);
            if (await db.ProjectQuotes.AnyAsync(q => q.OpportunityId == project, ct)) continue;

            List<ProjectQuoteLineInput> lines = [];
            foreach (var (code, quantity, discount) in offer.Lines)
            {
                var product = await administration.GetProductAsync(request.TenantId, code, ct);
                if (product is null) continue;
                lines.Add(new ProjectQuoteLineInput(null, product.Id, quantity,
                    DiscountKind: discount > 0 ? QuoteDiscountKind.Percent : QuoteDiscountKind.None, DiscountValue: discount));
            }
            if (lines.Count == 0) continue;

            using (BackgroundJobContext.SetScope(request.TenantId, owner, "Démo"))
            {
                Ok(await sender.Send(new SetProjectQuoteCommand(project, lines, QuoteDiscountKind.None, 0, null), ct), offer.Reference);
                offers++;
                if (offer.Issue && (await sender.Send(new IssueProjectQuoteCommand(project, null, null), ct)).IsSuccess) quotes++;
            }
        }
        if (offers > 0) created["Offres chiffrées"] = offers;
        if (quotes > 0) created["Devis PDF"] = quotes;
    }

    private static string Domain(string company) => new string(company.Normalize(System.Text.NormalizationForm.FormD)
        .Where(ch => ch < 128 && char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");
}
