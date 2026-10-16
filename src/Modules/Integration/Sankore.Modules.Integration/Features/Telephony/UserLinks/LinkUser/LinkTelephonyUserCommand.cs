namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Confirms the pairing of a CRM user with a provider account (TEL-B-05).
///
/// <para>
/// <b>Both sides are named by the caller, and that is the point of the story.</b> The directory
/// read proposes; nothing applies a proposal on its own, because a wrong link attributes one
/// agent's conversations — with the customer's number and the agent's note — to another.
/// </para>
///
/// <para>
/// <c>IResourceCommand</c>: who linked whom to what is exactly what an audit trail is asked for
/// afterwards, and <c>LinkedBy</c> comes from <c>ICurrentUser</c> and never from the body.
/// </para>
/// </summary>
/// <param name="DefaultCallerNumber">
/// Optional. The number this user's outbound calls present — the institution's own line, not a
/// private one. Null falls back to the connection's default, which is the ordinary case.
/// </param>
internal sealed record LinkTelephonyUserCommand(
    Guid CrmUserId,
    string ExternalUserId,
    string? ExternalEmail = null,
    string? DefaultCallerNumber = null)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyUserLink";

    /// <summary>
    /// The CRM user, because the row's own id does not exist yet when the audit row is written.
    /// The provider's account id is deliberately not here: an audit resource id is for finding
    /// the row again, and the payload already carries it.
    /// </summary>
    public string? ResourceId => CrmUserId.ToString();
}
