namespace Sankore.Modules.Notifications.Domain;

public enum EmailOutboxStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    DeadLettered
}
