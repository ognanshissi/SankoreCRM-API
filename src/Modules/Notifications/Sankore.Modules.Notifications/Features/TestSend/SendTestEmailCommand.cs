namespace Sankore.Modules.Notifications.Features.TestSend;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Sends one message through the tenant's configured provider, right now, bypassing the outbox.
/// The whole point is the ERROR: an administrator who has just typed an SMTP password needs the
/// provider's own rejection in front of them, not a message that quietly dead-letters an hour later.
/// </summary>
public sealed record SendTestEmailCommand(string RecipientEmail)
    : IRequest<Result<TestEmailResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "TenantNotificationSettings";
    public string? ResourceId => null;
}

public sealed record TestEmailResult(
    bool Delivered,
    string ProviderType,
    /// <summary>Whether the tenant's own relay/API was used, or the platform account.</summary>
    bool UsedTenantProvider,
    /// <summary>The provider's own words when it refused. Null on success.</summary>
    string? Error);
