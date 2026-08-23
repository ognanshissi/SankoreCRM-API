using Sankore.Shared.Kernel;

namespace Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Public contract exposed by the Notifications module.
/// Other modules reference only this interface — never the main assembly.
/// </summary>
public interface INotificationsModule
{
    /// <summary>
    /// Enqueues a transactional email for asynchronous delivery.
    /// The caller's transaction must be committed before calling this so that
    /// the email record is persisted atomically with the business operation
    /// (use the Outbox pattern inside the calling handler, or call this after
    /// SaveChangesAsync succeeds in the same unit of work).
    /// Returns the Id of the created EmailOutboxMessage.
    /// </summary>
    Task<Result<Guid>> QueueEmailAsync(QueueEmailRequest request, CancellationToken ct = default);
}
