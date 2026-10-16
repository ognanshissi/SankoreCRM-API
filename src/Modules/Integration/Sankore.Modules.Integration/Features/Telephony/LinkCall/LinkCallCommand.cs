namespace Sankore.Modules.Integration.Features.Telephony.LinkCall;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An agent says which record a call was about, or corrects it (TEL-B-09, TEL-B-10).
///
/// <para>
/// What makes <c>Ambiguous</c> useful: the journal deliberately refuses to guess between two
/// people who share a number, and this is how the human who just spoke to one of them settles it.
/// </para>
///
/// <para>
/// <b>The reason is mandatory and it is kept.</b> A conversation filed against the wrong person
/// is something a later reader — a compliance officer, the agent's successor — must be able to
/// understand, and "somebody changed it" does not explain a client's file mentioning a call they
/// never made.
/// </para>
/// </summary>
internal sealed record LinkCallCommand(Guid CallId, string EntityType, Guid EntityId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCall";

    public string? ResourceId => CallId.ToString();
}
