namespace Sankore.Admin.Domain;

public enum RenewalPeriod
{
    Monthly,
    Yearly
}

public record SubscriptionStateInfo (DateTimeOffset NextRenewalAt, RenewalPeriod RenewalPeriod);