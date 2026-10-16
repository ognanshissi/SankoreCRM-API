namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Sets, or clears, the number one user's outbound calls present (TEL-B-05).
///
/// <para>
/// Null CLEARS, and falls the user back on the connection's own default. That is the ordinary
/// state rather than an incomplete one, so there is no separate "reset" route to remember.
/// </para>
/// </summary>
internal sealed record SetTelephonyCallerNumberCommand(Guid LinkId, string? DefaultCallerNumber)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyUserLink";

    public string? ResourceId => LinkId.ToString();
}
