namespace Sankore.Modules.Leads.Features.ProjectTemplates;

using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// The ready-made « Dossier de crédit » (2026-11-05, product owner): the request, the file, the
/// analyst's opinion and the decision, then the core banking. The CRM carries the file up to the
/// decision and keeps no loan: disbursement, schedule and repayments are the core banking's.
///
/// <para>
/// Declared income is deliberately not a field: a project's fields are stored in clear, while the
/// client record encrypts it and reveals it through its audited path.
/// </para>
/// </summary>
internal static class CreditFileTemplate
{
    public const string Name = "Dossier de crédit";
    public const string CircuitName = "Analyse et décision de crédit";
    public const string TermFieldKey = "dureeMois";
    public const string PurposeFieldKey = "objetCredit";
    private const string GuaranteesFieldKey = "garanties";
    private const string IncomeProofFieldKey = "justificatifRevenus";

    public static IReadOnlyList<ProjectCircuitStep> CircuitSteps { get; } =
    [
        new("Avis de l'analyste", Roles.CreditAnalyst.Code, 48),
        new("Décision", Roles.BranchManager.Code, 48),
    ];

    public static IReadOnlyList<ProjectFieldInput> Fields { get; } =
    [
        new(TermFieldKey, "Durée (mois)", ProjectFieldType.Integer),
        new(PurposeFieldKey, "Objet du crédit", ProjectFieldType.Text),
        new(GuaranteesFieldKey, "Garanties proposées", ProjectFieldType.LongText),
        new(IncomeProofFieldKey, "Justificatifs de revenus", ProjectFieldType.Attachment),
    ];

    public static ProjectForecastOptions Forecast { get; } = new(TracksForecast: true, AmountKind: ProjectAmountKind.Loan);

    public static IReadOnlyList<ProjectStepInput> Steps(string circuit) =>
    [
        new(null, "Demande", OpportunityStage.Qualification, 20,
            "Noter le montant, la durée et l'objet du crédit."),
        new(null, "Constitution du dossier", OpportunityStage.NeedsAnalysis, 40,
            "Ouvrir la fiche client et réunir les pièces.",
            OpensClientFile: true,
            RequiresAmount: true,
            Checklist:
            [
                new(null, "Justificatifs de revenus reçus"),
                new(null, "Garanties vérifiées"),
            ],
            RequiredFieldKeys: [TermFieldKey, PurposeFieldKey]),
        new(null, "Analyse et décision", OpportunityStage.Proposal, 60,
            "L'analyste donne son avis, puis le chef d'agence décide.",
            WorkflowCircuit: circuit),
        new(null, "Accord", OpportunityStage.Negotiation, 90,
            "Le dossier part au core banking, qui gère ensuite le prêt.",
            IsApproval: true),
    ];

    /// <summary>The families whose products are loans, as the client's contracts read them.</summary>
    public static bool IsCreditCategory(string? category) =>
        Enum.TryParse<ProductCategory>(category, ignoreCase: true, out var parsed)
        && parsed is ProductCategory.Loan or ProductCategory.GroupCredit or ProductCategory.Agriculture;

    /// <summary>
    /// Whether a project of this template is sent on approval: a loan with a duration, which the
    /// core banking cannot do without. Keeps every other template, loans included, as it was.
    /// </summary>
    public static bool IsCreditFile(ResolvedProjectTemplate template) =>
        template.AmountKind == ProjectAmountKind.Loan
        && (template.Fields ?? []).Any(f => f.Key == TermFieldKey);

    /// <summary>A value the form stored as a number or as text; anything else reads as absent.</summary>
    public static (int? TermMonths, string? Purpose) ReadFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, null);

        int? term = null;
        if (root.TryGetProperty(TermFieldKey, out var months))
        {
            if (months.ValueKind == JsonValueKind.Number && months.TryGetInt32(out var number))
                term = number;
            else if (months.ValueKind == JsonValueKind.String
                     && int.TryParse(months.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                term = parsed;
        }

        var purpose = root.TryGetProperty(PurposeFieldKey, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

        return (term > 0 ? term : null, string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim());
    }
}

/// <summary>
/// Creates the ready-made template and its circuit. Not an <c>ICommand</c>: each of the two is its
/// own command with its own transaction, across two DbContexts. A circuit left behind by a template
/// that then failed is simply reused by the next attempt.
/// </summary>
/// <param name="ProductCode">
/// One credit product, which then follows it whatever its family's template says; null for the Loan
/// family. An institution that already has a family template can take the model product by product
/// without touching the projects on it.
/// </param>
internal sealed record CreateCreditFileTemplateCommand(string? ProductCode = null) : IRequest<Result<Guid>>;

internal sealed class CreateCreditFileTemplateHandler(
    LeadsDbContext db,
    IWorkflowModule workflow,
    IAdministrationModule administration,
    ITenantContext tenant,
    ISender sender)
    : IRequestHandler<CreateCreditFileTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCreditFileTemplateCommand command, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(command.ProductCode) ? null : command.ProductCode.Trim().ToUpperInvariant();
        if (code is not null)
        {
            var product = await administration.GetProductAsync(tenant.CurrentTenantId, code, ct);
            if (product is null) return Result.Fail<Guid>(ProjectTemplateErrors.UnknownProduct);
            if (!CreditFileTemplate.IsCreditCategory(product.Category))
                return Result.Fail<Guid>(ProjectTemplateErrors.NotCreditProduct);
        }

        var taken = await db.ProjectTemplates.AnyAsync(t =>
            code != null ? t.ProductCode == code : t.ProductCode == null && t.Category == ProductCategory.Loan, ct);
        if (taken) return Result.Fail<Guid>(ProjectTemplateErrors.ScopeTaken);

        var circuit = await workflow.EnsureProjectCircuitAsync(
            CreditFileTemplate.CircuitName,
            "L'avis de l'analyste puis la décision du chef d'agence sur un dossier de crédit.",
            CreditFileTemplate.CircuitSteps,
            ct);
        if (circuit.IsFailure) return Result.Fail<Guid>(circuit.Error!);

        return await sender.Send(new CreateProjectTemplateCommand(
            CreditFileTemplate.Name,
            code is null ? ProductCategory.Loan : null,
            code,
            CreditFileTemplate.Steps(circuit.Value),
            CreditFileTemplate.Forecast,
            CreditFileTemplate.Fields), ct);
    }
}
