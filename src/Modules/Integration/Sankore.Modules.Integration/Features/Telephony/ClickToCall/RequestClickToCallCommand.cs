namespace Sankore.Modules.Integration.Features.Telephony.ClickToCall;

using MediatR;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An agent asks for their handset to ring, then for a record's number to be dialled (TEL-B-06).
///
/// <para>
/// <b>A record and a slot, never a number.</b> See <see cref="DialableNumberResolver"/> for why
/// the number is read on the server: a command carrying one is a command a browser can be made
/// to post a stranger's number to, and the call would go out on the institution's line.
/// </para>
///
/// <para>
/// <c>ICommand</c>, so the request row and the audit row commit together — but the call to the
/// provider does NOT happen inside that transaction. See the handler.
/// </para>
/// </summary>
internal sealed record RequestClickToCallCommand(
    string EntityType,
    Guid EntityId,
    TelephonyPhoneSlot Slot)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyClickToCall";

    /// <summary>
    /// The RECORD, not the request row, which does not exist yet when the audit row is written.
    /// It is also the question an audit of this act is asked: whom did this agent ring.
    /// </summary>
    public string? ResourceId => EntityId.ToString();
}
