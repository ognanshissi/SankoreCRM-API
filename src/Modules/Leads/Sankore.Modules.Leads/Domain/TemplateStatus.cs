namespace Sankore.Modules.Leads.Domain;

public enum TemplateStatus
{
    /// <summary>Template is being configured — not yet available for qualification.</summary>
    Draft,
    /// <summary>Template is live and available for agents to use during qualification.</summary>
    Published,
    /// <summary>Template has been retired; existing responses are preserved for audit.</summary>
    Archived
}
