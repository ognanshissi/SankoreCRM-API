namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Owned entity — lives inside a <see cref="WorkflowTemplate"/>.
/// Describes one step in the approval circuit (order, role required, timeout).
/// </summary>
public sealed class WorkflowStepDefinition
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>1-based execution order within the template.</summary>
    public int Order { get; private set; }

    /// <summary>Machine-readable state code (e.g. "STEP_1"). Auto-set from Order.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Semantic type of this node in the state machine.</summary>
    public StateType StateType { get; private set; } = StateType.Approval;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Role code (e.g. "branchmanager") required to approve this step.</summary>
    public string? ApproverRoleCode { get; private set; }

    /// <summary>Optional SLA — how many hours the approver has to act.</summary>
    public int? TimeoutHours { get; private set; }

    /// <summary>Nature métier de l'étape (formulaire, KYC, condition…). Voir <see cref="StepKind"/>.</summary>
    public StepKind Kind { get; private set; } = StepKind.Validation;

    /// <summary>
    /// Paramètres propres au <see cref="Kind"/> (niveau KYC, escalade, branche de condition…), en
    /// JSON libre : leur forme évolue avec le constructeur sans migration à chaque champ.
    /// </summary>
    public string? ConfigJson { get; private set; }

    /// <summary>Le dossier ne peut pas avancer tant que cette étape n'est pas franchie.</summary>
    public bool IsBlocking { get; private set; }

    private readonly List<WorkflowRule> _rules = [];
    public IReadOnlyCollection<WorkflowRule> Rules => _rules.AsReadOnly();

    private WorkflowStepDefinition() { }

    public static WorkflowStepDefinition Create(
        Guid templateId,
        int order,
        string name,
        string? description = null,
        string? approverRoleCode = null,
        int? timeoutHours = null,
        StepKind kind = StepKind.Validation,
        string? configJson = null,
        bool isBlocking = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new Sankore.Shared.Kernel.DomainException("Step name is required.");
        if (order < 1)
            throw new Sankore.Shared.Kernel.DomainException("Step order must be >= 1.");

        return new WorkflowStepDefinition
        {
            Id               = Guid.NewGuid(),
            TemplateId       = templateId,
            Order            = order,
            Code             = $"STEP_{order}",
            StateType        = StateType.Approval,
            Name             = name.Trim(),
            Description      = description?.Trim(),
            ApproverRoleCode = approverRoleCode,
            TimeoutHours     = timeoutHours,
            Kind             = kind,
            ConfigJson       = configJson,
            IsBlocking       = isBlocking
        };
    }

    public WorkflowRule AddRule(RuleType ruleType, string field, RuleOperator op, string value, int logicalGroup = 0)
    {
        var rule = WorkflowRule.Create(TemplateId, Id, ruleType, field, op, value, logicalGroup);
        _rules.Add(rule);
        return rule;
    }

    public void RemoveRule(Guid ruleId)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId)
            ?? throw new Sankore.Shared.Kernel.DomainException("Rule not found.");
        _rules.Remove(rule);
    }

    public void Update(
        string name,
        string? description,
        string? approverRoleCode,
        int? timeoutHours,
        StepKind? kind = null,
        string? configJson = null,
        bool? isBlocking = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new Sankore.Shared.Kernel.DomainException("Step name is required.");

        Name = name.Trim();
        Description = description?.Trim();
        ApproverRoleCode = approverRoleCode;
        TimeoutHours = timeoutHours;
        // Champs optionnels à la mise à jour : un client antérieur au constructeur de parcours ne
        // les envoie pas, et ne doit pas effacer ce que le constructeur a enregistré.
        if (kind.HasValue) Kind = kind.Value;
        if (configJson is not null) ConfigJson = configJson.Length == 0 ? null : configJson;
        if (isBlocking.HasValue) IsBlocking = isBlocking.Value;
    }

    /// <summary>Déplace l'étape dans l'ordre d'exécution. Le code suit, comme à la création.</summary>
    internal void MoveTo(int order)
    {
        Order = order;
        Code  = $"STEP_{order}";
    }
}
