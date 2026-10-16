namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Switches a link off, with the reason recorded (TEL-B-05).
///
/// <para>
/// <b>Logical, never a delete.</b> The agent's calls already in the journal point at this row,
/// and it is what explains why nothing new is being attributed to them. A deleted row would leave
/// a journal full of calls whose owner is unexplained — and the first thing somebody would do
/// about that is create the link again, re-attributing nothing retroactively.
/// </para>
///
/// <para>
/// <c>Reason</c> is mandatory and is the story's own word, <i>« déliaison motivée et auditée »</i>:
/// an agent leaving, a provider account recycled for somebody else, and a mistaken pairing are
/// three different facts, and only the first means the person stopped working here.
/// </para>
/// </summary>
internal sealed record UnlinkTelephonyUserCommand(Guid LinkId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyUserLink";

    public string? ResourceId => LinkId.ToString();
}
