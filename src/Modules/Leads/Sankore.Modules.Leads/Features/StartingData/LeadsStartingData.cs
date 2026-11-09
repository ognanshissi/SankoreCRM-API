using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.StartTestingLeadSource;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.StartingData;

/// <summary>
/// The « Démarrage » share of Leads (<c>docs/donnees-demarrage-demo.md</c>, 2026-11-08): the usual
/// lead sources, the ready-made « Dossier de crédit » for the Loan family, and a published
/// qualification questionnaire per main family — through the ordinary commands, after Administration
/// has created the products. Idempotent: a source by its code, the credit template by its scope, a
/// questionnaire by its family (any template of that family, published or not, is the institution's).
/// </summary>
internal sealed class LeadsStartingData : ITenantStartingData
{
    public int Order => 20;
    public string Module => "Sources, gabarits et questionnaires";

    /// <summary>Sources an institution receives leads through without any integration: active at once.</summary>
    internal static readonly IReadOnlyList<(string Code, string Label, LeadChannelType Channel)> InternalSources =
    [
        ("AGENCE", "Passage en agence", LeadChannelType.WalkIn),
        ("PARRAINAGE", "Parrainage", LeadChannelType.Referral),
        ("APPEL-ENTRANT", "Appel entrant", LeadChannelType.InboundCall),
        ("IMPORT-FICHIER", "Import de fichier", LeadChannelType.FileImport),
    ];

    /// <summary>The web form needs the institution's site (allowed origins): left in draft.</summary>
    internal const string WebFormCode = "SITE-WEB";

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<LeadsDbContext>();
        var sender = services.GetRequiredService<ISender>();
        var modules = services.GetRequiredService<ITenantModules>();
        var created = new Dictionary<string, int>();

        var codes = (await db.LeadSourceConfigs.Select(s => s.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = 0;
        var order = codes.Count;
        foreach (var (code, label, channel) in InternalSources)
        {
            if (codes.Contains(code)) continue;
            var id = Ok(await sender.Send(new CreateLeadSourceCommand(
                request.TenantId, code, label, channel, ++order, IntegrationMode.Internal), ct), code);
            Ok(await sender.Send(new StartTestingLeadSourceCommand(id), ct), code);
            Ok(await sender.Send(new ActivateLeadSourceCommand(id), ct), code);
            sources++;
        }
        if (!codes.Contains(WebFormCode))
        {
            Ok(await sender.Send(new CreateLeadSourceCommand(
                request.TenantId, WebFormCode, "Formulaire du site web", LeadChannelType.WebForm, ++order,
                IntegrationMode.EmbeddedScript,
                "À configurer : adresse du site autorisée, puis activation."), ct), WebFormCode);
            sources++;
        }
        if (sources > 0) created["Sources de lead"] = sources;

        if (await modules.AllowsCategoryAsync(ProductCategory.Loan, ct)
            && !await db.ProjectTemplates.AnyAsync(t => t.ProductCode == null && t.Category == ProductCategory.Loan, ct))
        {
            Ok(await sender.Send(new CreateCreditFileTemplateCommand(), ct), CreditFileTemplate.Name);
            created["Gabarits de projet"] = 1;
        }

        var questionnaires = 0;
        foreach (var (category, name, questions) in Questionnaires)
        {
            if (!await modules.AllowsCategoryAsync(category, ct)
                || await db.QualificationTemplates.AnyAsync(t => t.ProductCategory == category && t.ProductCode == null, ct))
                continue;
            var id = Ok(await sender.Send(new CreateQualificationTemplateCommand(
                request.TenantId, name, "Questionnaire de départ, à adapter à vos produits.", category, null, questions, null), ct), name);
            Ok(await sender.Send(new PublishQualificationTemplateCommand(id), ct), name);
            questionnaires++;
        }
        if (questionnaires > 0) created["Questionnaires"] = questionnaires;

        return created;
    }

    internal static readonly IReadOnlyList<(ProductCategory Category, string Name, IReadOnlyList<QuestionInput> Questions)> Questionnaires =
    [
        (ProductCategory.Loan, "Qualification crédit",
        [
            new("Montant souhaité (FCFA)", QuestionType.Numeric, 3, IsRequired: true, MinValue: 0, MaxValue: 50_000_000),
            new("Le prospect a-t-il une activité génératrice de revenus ?", QuestionType.YesNo, 4, IsRequired: true),
            new("Ancienneté de l'activité", QuestionType.SingleChoice, 3, Options: ["Moins d'un an", "1 à 3 ans", "Plus de 3 ans"]),
            new("Peut-il présenter une garantie ou une caution ?", QuestionType.YesNo, 2),
            new("Objet du crédit", QuestionType.Text, 1),
        ]),
        (ProductCategory.Savings, "Qualification épargne",
        [
            new("Versement mensuel envisagé (FCFA)", QuestionType.Numeric, 3, MinValue: 0, MaxValue: 5_000_000),
            new("Le prospect a-t-il déjà un compte dans une autre institution ?", QuestionType.YesNo, 1),
            new("Objectif de l'épargne", QuestionType.SingleChoice, 2, Options: ["Projet", "Précaution", "Scolarité", "Autre"]),
        ]),
        (ProductCategory.HealthInsurance, "Qualification assurance santé",
        [
            new("Nombre de personnes à couvrir", QuestionType.Numeric, 3, IsRequired: true, MinValue: 1, MaxValue: 15),
            new("Le foyer a-t-il déjà une couverture santé ?", QuestionType.YesNo, 2),
            new("Budget mensuel envisagé (FCFA)", QuestionType.Numeric, 3, MinValue: 0, MaxValue: 500_000),
        ]),
    ];

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");

    private static void Ok(Result result, string what)
    {
        if (result.IsFailure) throw new InvalidOperationException($"{what} : {result.Error}");
    }
}
