namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The steps a project (<see cref="Opportunity"/>) goes through, for one scope of the catalogue.
///
/// <para>
/// <b>Three scopes, resolved like the qualification templates</b>: a product line's own template
/// (<see cref="ProductCode"/>), else its family's (<see cref="Category"/>), else the tenant's
/// default (both null). A tenant that never saved a default gets <see cref="BuiltInDefault"/>,
/// which reproduces the five stages the screens showed before templates existed — so nothing
/// moves for a tenant that never opens the settings.
/// </para>
///
/// <para>
/// <b>Every step belongs to one of the five open phases</b> of <see cref="OpportunityStage"/>,
/// and the phases never go backwards along the order. The phase is what reports, the duplicate
/// check and the closing still read: a template renames and splits the funnel, it does not
/// replace it.
/// </para>
/// </summary>
public sealed class ProjectTemplate : AggregateRoot
{
    public const int MaxSteps = 12;
    public const int MaxLabelLength = 60;
    public const int MaxSuggestionLength = 200;
    public const int MaxChecklistItems = 10;
    public const int MaxChecklistLabelLength = 120;
    public const int MaxCircuitLength = 60;
    public const int MaxNameLength = 100;

    public static readonly IReadOnlyList<OpportunityStage> OpenPhases =
    [
        OpportunityStage.Prospecting,
        OpportunityStage.Qualification,
        OpportunityStage.NeedsAnalysis,
        OpportunityStage.Proposal,
        OpportunityStage.Negotiation,
    ];

    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;

    /// <summary>The family this template serves; null for a product's own template and for the default.</summary>
    public ProductCategory? Category { get; private set; }

    /// <summary>The product line's code (upper-case, as M12 stores it); null unless product-specific.</summary>
    public string? ProductCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Whether the projects of this template carry a sales FORECAST: a probability of signing and
    /// an expected close date, shown to the agent and summed into the weighted forecast of the
    /// reports. On for long sales whose outcome is uncertain (credit, insurance, term deposits);
    /// off for a product sold in one or two meetings, where both would be decoration the agent is
    /// asked to fill. The step probabilities are still stored either way.
    /// </summary>
    public bool TracksForecast { get; private set; } = true;

    /// <summary>
    /// What a project's amount IS for this product: a loan to disburse, a premium to collect, a
    /// deposit to collect. The forecast never adds two kinds together — a disbursement and a deposit
    /// are flows in opposite directions — and the screens name the amount accordingly.
    /// </summary>
    public ProjectAmountKind AmountKind { get; private set; } = ProjectAmountKind.Generic;

    /// <summary>
    /// The close date is a deadline, not an estimate (a seasonal crop loan is useless once the rains
    /// have started): a project cannot be saved without one, and a passed date raises an urgent task
    /// for whoever follows the project. Requires <see cref="TracksForecast"/>.
    /// </summary>
    public bool CriticalCloseDate { get; private set; }

    private readonly List<ProjectTemplateStep> _steps = [];
    public IReadOnlyList<ProjectTemplateStep> Steps => _steps.OrderBy(s => s.Order).ToList();

    public bool IsDefault => Category is null && ProductCode is null;

    private ProjectTemplate() { } // EF Core

    public static Result<ProjectTemplate> Create(
        Guid tenantId,
        string name,
        ProductCategory? category,
        string? productCode,
        IReadOnlyList<ProjectStepInput> steps,
        DateTimeOffset now,
        ProjectForecastOptions? forecast = null)
    {
        if (category is not null && !string.IsNullOrWhiteSpace(productCode))
            return Result.Fail<ProjectTemplate>(ProjectTemplateErrors.ScopeInvalid);

        var template = new ProjectTemplate
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            Category    = category,
            ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim().ToUpperInvariant(),
            CreatedAt   = now,
        };

        var updated = template.Update(name, steps, now, forecast);
        return updated.IsSuccess ? Result.Ok(template) : Result.Fail<ProjectTemplate>(updated.Error!);
    }

    /// <summary>
    /// Renames the template and replaces its steps. A step sent back with its id is the SAME step,
    /// edited in place — the projects standing on it stay there; a step left out is removed, and
    /// its projects fall back to the step of their phase.
    /// </summary>
    public Result Update(
        string name, IReadOnlyList<ProjectStepInput> steps, DateTimeOffset now, ProjectForecastOptions? forecast = null)
    {
        forecast ??= ProjectForecastOptions.Default;

        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail(ProjectTemplateErrors.NameRequired);
        if (name.Trim().Length > MaxNameLength)
            return Result.Fail(ProjectTemplateErrors.NameTooLong);

        var invalid = Validate(steps);
        if (invalid is not null)
            return Result.Fail(invalid);
        if (forecast.CriticalCloseDate && !forecast.TracksForecast)
            return Result.Fail(ProjectTemplateErrors.CriticalDateWithoutForecast);
        if (!Enum.IsDefined(forecast.AmountKind))
            return Result.Fail(ProjectTemplateErrors.AmountKindInvalid);

        Name = name.Trim();
        TracksForecast = forecast.TracksForecast;
        AmountKind = forecast.AmountKind;
        CriticalCloseDate = forecast.CriticalCloseDate;

        var incomingIds = steps.Where(s => s.Id is not null).Select(s => s.Id!.Value).ToHashSet();
        _steps.RemoveAll(s => !incomingIds.Contains(s.Id));

        for (var i = 0; i < steps.Count; i++)
        {
            var input = steps[i];
            var existing = input.Id is { } id ? _steps.FirstOrDefault(s => s.Id == id) : null;

            // Edited in place rather than removed and re-added: EF tracks an owned row by its key,
            // and a delete plus an insert of the same key in one save is refused.
            if (existing is not null)
                existing.Set(input, i);
            else
                _steps.Add(ProjectTemplateStep.From(input.Id ?? Guid.NewGuid(), input, i));
        }

        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>The first error the steps carry, or null.</summary>
    private static string? Validate(IReadOnlyList<ProjectStepInput> steps)
    {
        if (steps.Count == 0) return ProjectTemplateErrors.NoStep;
        if (steps.Count > MaxSteps) return ProjectTemplateErrors.TooManySteps;

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var previousPhase = -1;

        if (steps.Count(s => s.OpensClientFile) > 1) return ProjectTemplateErrors.TooManyClientFileSteps;
        if (steps.Count(s => s.IsApproval) > 1) return ProjectTemplateErrors.TooManyApprovalSteps;
        if (steps.Count(s => s.RequiresQuestionnaires) > 1) return ProjectTemplateErrors.TooManyQuestionnaireSteps;

        // The client file opens before the decision, never after: approving a project whose file
        // has not been opened would approve someone nobody identified.
        var opens = steps.ToList().FindIndex(s => s.OpensClientFile);
        var approval = steps.ToList().FindIndex(s => s.IsApproval);
        if (opens >= 0 && approval >= 0 && approval < opens) return ProjectTemplateErrors.ApprovalBeforeClientFile;

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Label)) return ProjectTemplateErrors.StepLabelRequired;
            if (step.Label.Trim().Length > MaxLabelLength) return ProjectTemplateErrors.StepLabelTooLong;
            if (!labels.Add(step.Label.Trim())) return ProjectTemplateErrors.StepLabelDuplicate;
            if (step.Id is { } id && !ids.Add(id)) return ProjectTemplateErrors.StepIdDuplicate;

            // 100 is a won project and 0 a lost one: an open step is strictly between.
            if (step.Probability is < 1 or > 99) return ProjectTemplateErrors.StepProbabilityOutOfRange;
            if (step.Suggestion?.Trim().Length > MaxSuggestionLength) return ProjectTemplateErrors.StepSuggestionTooLong;
            if (step.WorkflowCircuit?.Trim().Length > MaxCircuitLength) return ProjectTemplateErrors.StepWorkflowInvalid;

            var checklist = step.Checklist ?? [];
            if (checklist.Count > MaxChecklistItems) return ProjectTemplateErrors.StepChecklistTooLong;
            var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in checklist)
            {
                if (string.IsNullOrWhiteSpace(item.Label)) return ProjectTemplateErrors.StepChecklistItemRequired;
                if (item.Label.Trim().Length > MaxChecklistLabelLength) return ProjectTemplateErrors.StepChecklistItemTooLong;
                if (!items.Add(item.Label.Trim())) return ProjectTemplateErrors.StepChecklistItemDuplicate;
            }

            var phase = OpenPhases.ToList().IndexOf(step.Phase);
            if (phase < 0) return ProjectTemplateErrors.StepPhaseInvalid;
            if (phase < previousPhase) return ProjectTemplateErrors.StepPhasesOutOfOrder;
            previousPhase = phase;
        }

        return null;
    }

    // ── The template a tenant has before saving one ─────────────────────────────

    public const string BuiltInDefaultName = "Parcours standard";

    /// <summary>
    /// The five stages as they were, with FIXED step ids: a project moved onto one of them before
    /// the tenant saves its default keeps pointing at a step the saved copy still has, as long as
    /// the screen sends the ids back — which it does.
    /// </summary>
    public static readonly IReadOnlyList<ProjectStepInput> BuiltInDefault =
    [
        new(new Guid("6f1c0a10-0000-4000-8000-000000000001"), "Prospection", OpportunityStage.Prospecting, 10, "Planifier un rendez-vous"),
        new(new Guid("6f1c0a10-0000-4000-8000-000000000002"), "Qualification", OpportunityStage.Qualification, 20, "Vérifier l'éligibilité"),
        new(new Guid("6f1c0a10-0000-4000-8000-000000000003"), "Étude", OpportunityStage.NeedsAnalysis, 40, "Réunir les pièces du dossier"),
        new(new Guid("6f1c0a10-0000-4000-8000-000000000004"), "Proposition", OpportunityStage.Proposal, 60, "Présenter l'offre"),
        new(new Guid("6f1c0a10-0000-4000-8000-000000000005"), "Négociation", OpportunityStage.Negotiation, 80, "Fixer la date de signature"),
    ];
}

/// <summary>One step of a <see cref="ProjectTemplate"/>. Keyed by (template, id): see the configuration.</summary>
public sealed class ProjectTemplateStep
{
    public Guid Id { get; private set; }
    public string Label { get; private set; } = default!;
    public OpportunityStage Phase { get; private set; }

    /// <summary>Chance of signing at this step, in percent (1–99).</summary>
    public int Probability { get; private set; }

    /// <summary>The next action the screen proposes when a project reaches this step.</summary>
    public string? Suggestion { get; private set; }

    /// <summary>
    /// Reaching this step needs a client record — for a product that requires KYC, this is where
    /// the client file and its KYC file open. See <c>ProjectStepGate</c>.
    /// </summary>
    public bool OpensClientFile { get; private set; }

    /// <summary>
    /// The decision step. Reaching it — or winning the project — needs a client, and for a product
    /// that requires KYC, a file approved at the tier the product asks for.
    /// </summary>
    public bool IsApproval { get; private set; }

    /// <summary>
    /// Reaching this step needs every questionnaire the product asks — of the file and of each
    /// person — answered. Winning a project needs the per-person ones even when no step is marked.
    /// </summary>
    public bool RequiresQuestionnaires { get; private set; }

    /// <summary>Reaching this step needs the project's amount filled in.</summary>
    public bool RequiresAmount { get; private set; }

    /// <summary>Reaching this step needs the project's expected close date filled in.</summary>
    public bool RequiresCloseDate { get; private set; }

    /// <summary>
    /// Points the agent ticks WHILE on this step (« visite du chantier faite »…); leaving it —
    /// forward, or by winning — needs every one ticked (product owner, 2026-10-24). An item keeps
    /// its id across edits of the template, so the ticks already given survive a reworded label.
    /// </summary>
    public List<ProjectChecklistItem> Checklist { get; private set; } = [];

    /// <summary>
    /// The workflow circuit (M12, entity type « Opportunity ») started when a project REACHES this
    /// step; leaving the step needs that run approved, and a refusal blocks the project until it
    /// is lost or the circuit is run again. Names a circuit, never a template id: a new version of
    /// the workflow keeps the link.
    /// </summary>
    public string? WorkflowCircuit { get; private set; }

    public int Order { get; private set; }

    private ProjectTemplateStep() { } // EF Core

    internal static ProjectTemplateStep From(Guid id, ProjectStepInput input, int order)
    {
        var step = new ProjectTemplateStep { Id = id };
        step.Set(input, order);
        return step;
    }

    internal void Set(ProjectStepInput input, int order)
    {
        Label       = input.Label.Trim();
        Phase       = input.Phase;
        Probability = input.Probability;
        Suggestion  = string.IsNullOrWhiteSpace(input.Suggestion) ? null : input.Suggestion.Trim();
        OpensClientFile = input.OpensClientFile;
        IsApproval      = input.IsApproval;
        RequiresQuestionnaires = input.RequiresQuestionnaires;
        RequiresAmount    = input.RequiresAmount;
        RequiresCloseDate = input.RequiresCloseDate;
        Checklist = (input.Checklist ?? [])
            .Select(i => new ProjectChecklistItem(i.Id ?? Guid.NewGuid(), i.Label.Trim()))
            .ToList();
        WorkflowCircuit = string.IsNullOrWhiteSpace(input.WorkflowCircuit) ? null : input.WorkflowCircuit.Trim();
        Order       = order;
    }
}

/// <param name="Id">Null for a new step; an existing step's id keeps the projects standing on it.</param>
public sealed record ProjectStepInput(
    Guid? Id,
    string Label,
    OpportunityStage Phase,
    int Probability,
    string? Suggestion = null,
    bool OpensClientFile = false,
    bool IsApproval = false,
    bool RequiresQuestionnaires = false,
    bool RequiresAmount = false,
    bool RequiresCloseDate = false,
    IReadOnlyList<ProjectChecklistItemInput>? Checklist = null,
    string? WorkflowCircuit = null);

/// <param name="Id">Send back an existing item's id: the ticks already given on it stay.</param>
public sealed record ProjectChecklistItemInput(Guid? Id, string Label);

/// <summary>One point of a step's checklist.</summary>
public sealed record ProjectChecklistItem(Guid Id, string Label);

public static class ProjectTemplateErrors
{
    public const string NotFound = "PROJECT_TEMPLATE_NOT_FOUND";
    public const string ScopeTaken = "PROJECT_TEMPLATE_SCOPE_TAKEN";
    public const string ScopeInvalid = "PROJECT_TEMPLATE_SCOPE_INVALID";
    public const string UnknownProduct = "PROJECT_TEMPLATE_UNKNOWN_PRODUCT";
    public const string NameRequired = "PROJECT_TEMPLATE_NAME_REQUIRED";
    public const string NameTooLong = "PROJECT_TEMPLATE_NAME_TOO_LONG";
    public const string NoStep = "PROJECT_TEMPLATE_NO_STEP";
    public const string TooManySteps = "PROJECT_TEMPLATE_TOO_MANY_STEPS";
    public const string StepLabelRequired = "PROJECT_STEP_LABEL_REQUIRED";
    public const string StepLabelTooLong = "PROJECT_STEP_LABEL_TOO_LONG";
    public const string StepLabelDuplicate = "PROJECT_STEP_LABEL_DUPLICATE";
    public const string StepIdDuplicate = "PROJECT_STEP_ID_DUPLICATE";
    public const string StepProbabilityOutOfRange = "PROJECT_STEP_PROBABILITY_OUT_OF_RANGE";
    public const string StepSuggestionTooLong = "PROJECT_STEP_SUGGESTION_TOO_LONG";
    public const string StepPhaseInvalid = "PROJECT_STEP_PHASE_INVALID";
    public const string StepPhasesOutOfOrder = "PROJECT_STEP_PHASES_OUT_OF_ORDER";
    public const string StepNotInTemplate = "PROJECT_STEP_NOT_IN_TEMPLATE";
    public const string TooManyClientFileSteps = "PROJECT_TEMPLATE_TOO_MANY_CLIENT_FILE_STEPS";
    public const string TooManyApprovalSteps = "PROJECT_TEMPLATE_TOO_MANY_APPROVAL_STEPS";
    public const string ApprovalBeforeClientFile = "PROJECT_TEMPLATE_APPROVAL_BEFORE_CLIENT_FILE";
    public const string TooManyQuestionnaireSteps = "PROJECT_TEMPLATE_TOO_MANY_QUESTIONNAIRE_STEPS";

    /// <summary>A critical close date is a forecast setting: it means nothing on a template that hides the date.</summary>
    public const string CriticalDateWithoutForecast = "PROJECT_TEMPLATE_CRITICAL_DATE_WITHOUT_FORECAST";
    public const string AmountKindInvalid = "PROJECT_TEMPLATE_AMOUNT_KIND_INVALID";

    /// <summary>The project's template makes the close date a deadline — or a step needs it — and none was given.</summary>
    public const string CloseDateRequired = "PROJECT_CLOSE_DATE_REQUIRED";

    public const string StepChecklistTooLong = "PROJECT_STEP_CHECKLIST_TOO_LONG";
    public const string StepChecklistItemRequired = "PROJECT_STEP_CHECKLIST_ITEM_REQUIRED";
    public const string StepChecklistItemTooLong = "PROJECT_STEP_CHECKLIST_ITEM_TOO_LONG";
    public const string StepChecklistItemDuplicate = "PROJECT_STEP_CHECKLIST_ITEM_DUPLICATE";
    public const string StepWorkflowInvalid = "PROJECT_STEP_WORKFLOW_INVALID";

    /// <summary>A step on the way needs the project's amount.</summary>
    public const string AmountRequired = "PROJECT_AMOUNT_REQUIRED";

    /// <summary>A step being left still has unticked points.</summary>
    public const string ChecklistIncomplete = "PROJECT_CHECKLIST_INCOMPLETE";

    /// <summary>The checklist item is not one of the project's template.</summary>
    public const string ChecklistItemUnknown = "PROJECT_CHECKLIST_ITEM_UNKNOWN";

    /// <summary>The step's validation circuit has not approved yet (or was never started).</summary>
    public const string WorkflowPending = "PROJECT_WORKFLOW_PENDING";

    /// <summary>The step's validation circuit refused: lose the project, or run the circuit again.</summary>
    public const string WorkflowRejected = "PROJECT_WORKFLOW_REJECTED";

    /// <summary>The circuit could not be started or read — fail-closed.</summary>
    public const string WorkflowUnavailable = "PROJECT_WORKFLOW_UNAVAILABLE";

    /// <summary>Running the circuit again is only for a current step whose last run was refused or never started.</summary>
    public const string WorkflowNotRestartable = "PROJECT_WORKFLOW_NOT_RESTARTABLE";

    /// <summary>A questionnaire the product asks — of the file or of a person — is unanswered.</summary>
    public const string QuestionnairesIncomplete = "PROJECT_QUESTIONNAIRES_INCOMPLETE";

    /// <summary>The project needs a client record first: convert its lead (or attach it).</summary>
    public const string ClientFileRequired = "PROJECT_CLIENT_FILE_REQUIRED";

    /// <summary>The client's KYC file is not approved — or there is none.</summary>
    public const string KycNotApproved = "PROJECT_KYC_NOT_APPROVED";

    /// <summary>The file is approved, below the tier the product requires.</summary>
    public const string KycTierInsufficient = "PROJECT_KYC_TIER_INSUFFICIENT";
}

/// <summary>What a project's amount stands for — see <see cref="ProjectTemplate.AmountKind"/>.</summary>
public enum ProjectAmountKind
{
    /// <summary>« Montant estimé » — no particular flow.</summary>
    Generic,
    /// <summary>The loan amount, to be disbursed.</summary>
    Loan,
    /// <summary>The (annual) premium, to be collected.</summary>
    Premium,
    /// <summary>The deposit, to be collected.</summary>
    Deposit,
}

/// <summary>The forecast settings of a template, set together.</summary>
public sealed record ProjectForecastOptions(
    bool TracksForecast = true,
    ProjectAmountKind AmountKind = ProjectAmountKind.Generic,
    bool CriticalCloseDate = false)
{
    public static readonly ProjectForecastOptions Default = new();
}
