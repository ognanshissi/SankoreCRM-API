namespace Sankore.Modules.Leads.Domain;

public enum ConsentStatus
{
    Active,
    Withdrawn,
    Expired,

    /// <summary>
    /// The prospect was asked and said no. Recorded, not merely absent: « non recueilli » and
    /// « refusé » are different answers, and only the second is a decision a regulator can be shown
    /// with its proof. Never satisfies anything — every check reads <see cref="Active"/>.
    /// </summary>
    Refused
}
