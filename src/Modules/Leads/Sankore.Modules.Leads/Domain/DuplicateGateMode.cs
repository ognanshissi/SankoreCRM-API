namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Controls how the system reacts when potential duplicates are detected during lead creation.
/// </summary>
public enum DuplicateGateMode
{
    /// <summary>
    /// Hard block: the lead is NOT created. Returns HTTP 409 Conflict with the list of
    /// potential duplicates. The caller must re-submit with <c>Force=true</c> to override.
    /// </summary>
    Block,

    /// <summary>
    /// Soft warning: the lead IS created, but the response includes <c>DuplicateDetected=true</c>
    /// and <c>PotentialDuplicates</c> so the caller can surface the information to the user.
    /// No re-submission is required.
    /// </summary>
    Warn
}
