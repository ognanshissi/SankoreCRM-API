namespace Sankore.Modules.Leads.Domain;

/// <summary>What the rule does when its trigger condition is satisfied.</summary>
public enum QuestionRuleAction
{
    /// <summary>Show this question (overrides a default-hidden question).</summary>
    Show,
    /// <summary>Hide this question and exclude it from scoring and validation.</summary>
    Hide,
    /// <summary>Force this question to be required, even if IsRequired = false.</summary>
    Require
}
