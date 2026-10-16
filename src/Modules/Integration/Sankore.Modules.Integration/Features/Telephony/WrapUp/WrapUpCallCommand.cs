namespace Sankore.Modules.Integration.Features.Telephony.WrapUp;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The agent says how the call went (TEL-B-11).
///
/// <para>
/// <b>The note is the one free-text field in this family, and it is encrypted.</b> An agent
/// writes what was said, which is as personal as the number — and unlike the number it is not
/// predictable, so it is the field most likely to carry something nobody anticipated. It is
/// stored under this module's key, never projected into a timeline summary, and never logged.
/// </para>
/// </summary>
/// <param name="CallbackAt">
/// Required when the outcome's own <c>RequiresCallbackDate</c> says so, and the referential
/// decides which outcomes those are. A date on an outcome that does not ask for one is kept all
/// the same: an agent noting "call back Thursday" on a "Joint" outcome is doing something
/// sensible, and refusing it would teach them to pick the wrong outcome to get the field.
/// </param>
internal sealed record WrapUpCallCommand(
    Guid CallId,
    string OutcomeCode,
    string? Note,
    DateTimeOffset? CallbackAt)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCall";

    public string? ResourceId => CallId.ToString();
}
