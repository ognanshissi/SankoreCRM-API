namespace Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;

using FluentValidation;

/// <summary>
/// Bounds, not business rules.
///
/// <para>
/// The ceilings mirror the columns (<c>reported_version</c> 50, <c>reported_status_detail</c> 500)
/// so an over-long value is refused with a message naming the field rather than failing at the
/// database on an anonymous, machine-driven endpoint — where the symptom would be an agent
/// retrying for ever.
/// </para>
///
/// <para>
/// A negative latency is refused rather than silently dropped. The aggregate maps it to null,
/// which is the right defensive behaviour, but an agent sending one has a bug in its own
/// measurement and a 422 is how it finds out — a null that quietly appears in an operator's view
/// would be read as "the agent did not measure", which is a different fact.
/// </para>
/// </summary>
internal sealed class RecordRelayHeartbeatValidator : AbstractValidator<RecordRelayHeartbeatCommand>
{
    /// <summary>
    /// Five minutes. Not a health threshold — nothing in this US decides when a relay is too slow
    /// to use — but a value above it is a unit mistake (seconds reported as milliseconds, a clock
    /// difference) rather than a measurement, and storing it would put a nonsense figure on an
    /// operator's screen.
    /// </summary>
    private const int MaxLatencyMs = 300_000;

    public RecordRelayHeartbeatValidator()
    {
        RuleFor(x => x.CertificateThumbprint)
            .Must(RelayCertificateThumbprint.IsWellFormed)
            .OverridePropertyName("certificateThumbprint")
            .WithMessage(RelayCertificateThumbprint.Requirement);

        RuleFor(x => x.Version)
            .MaximumLength(50)
            .When(x => x.Version is not null)
            .OverridePropertyName("version")
            .WithMessage("A reported version cannot exceed 50 characters.");

        RuleFor(x => x.LatencyMs)
            .InclusiveBetween(0, MaxLatencyMs)
            .When(x => x.LatencyMs is not null)
            .OverridePropertyName("latencyMs")
            .WithMessage($"A reported latency must be between 0 and {MaxLatencyMs} milliseconds.");

        RuleFor(x => x.StatusDetail)
            .MaximumLength(500)
            .When(x => x.StatusDetail is not null)
            .OverridePropertyName("statusDetail")
            .WithMessage("A reported status detail cannot exceed 500 characters.");
    }
}
