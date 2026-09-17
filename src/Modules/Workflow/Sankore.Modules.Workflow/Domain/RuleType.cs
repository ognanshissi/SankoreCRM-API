namespace Sankore.Modules.Workflow.Domain;

public enum RuleType
{
    /// <summary>Step is silently bypassed when the condition evaluates to true.</summary>
    SkipIf,

    /// <summary>Step is auto-approved (no human action) when the condition evaluates to true.</summary>
    AutoApproveIf,

    /// <summary>Step only participates when the condition evaluates to true; skipped otherwise.</summary>
    RequireIf
}
