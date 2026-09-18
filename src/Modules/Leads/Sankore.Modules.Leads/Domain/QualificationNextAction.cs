namespace Sankore.Modules.Leads.Domain;

/// <summary>Suggested follow-up action returned by the Qualify endpoint based on the computed score.</summary>
public enum QualificationNextAction
{
    /// <summary>Score ≥ 60 — lead is qualified and ready to be dispatched.</summary>
    DispatchToAgent,
    /// <summary>Score 40-59 — additional information is needed before dispatching.</summary>
    CollectMoreData,
    /// <summary>Score &lt; 40 — lead does not meet eligibility thresholds.</summary>
    Disqualify
}
