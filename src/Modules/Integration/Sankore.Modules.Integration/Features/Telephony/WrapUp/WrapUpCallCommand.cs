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
/// <param name="KeepRecording">
/// The agent asking for this conversation to be kept (TEL-B-15, <c>OnDemand</c>).
///
/// <para>
/// <b>This is the only route by which a recording is ever kept under <c>OnDemand</c>, and until
/// now there was none.</b> <c>TelRecordingPolicy.Keeps</c> reads <c>TelCall.KeepRecording</c> for
/// that mode and <c>MarkForRecording</c> existed on the aggregate with no caller — so the mode
/// was configurable and unreachable, which is worse than absent: a compliance officer could
/// select it, believe recording was on demand, and get nothing.
/// </para>
///
/// <para>
/// <b>It is ignored outside <c>OnDemand</c>, deliberately, rather than refused.</b> Under
/// <c>AllCalls</c> every call is kept and the flag is redundant; under <c>Disabled</c> nothing is
/// kept and honouring it would be a way around the policy. A screen that sends it anyway — an
/// agent whose tab predates a policy change — gets a successful wrap-up and no recording, which
/// is the behaviour the policy states.
/// </para>
///
/// <para>
/// Write-once in the aggregate's sense: it is only ever set to true. An agent who ticks the box
/// and wraps up again cannot untick it, because by then the fetch job may already hold the
/// audio, and "forget it" is a retention decision for the purge or for an anonymisation — not
/// for a second wrap-up.
/// </para>
/// </param>
internal sealed record WrapUpCallCommand(
    Guid CallId,
    string OutcomeCode,
    string? Note,
    DateTimeOffset? CallbackAt,
    bool KeepRecording = false)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCall";

    public string? ResourceId => CallId.ToString();
}
