namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>How somebody got at a recording (TEL-B-17).</summary>
public enum TelRecordingAccessKind
{
    /// <summary>Streamed through the audited player. The bytes stayed inside a request.</summary>
    Listen,

    /// <summary>
    /// Downloaded OUT of the platform. A different act and a different permission: past this
    /// point no control of ours applies to the file, which is why the reason is mandatory.
    /// </summary>
    Export,
}

/// <summary>
/// One access to one recording (TEL-B-17).
///
/// <para>
/// <b>Written in the SAME transaction as the bytes are handed over</b>, so there is no window in
/// which a voice was heard without a trace. The same discipline M01's
/// <c>RevealSensitiveFieldHandler</c> applies to a document number, for a datum that is arguably
/// more personal: a recording is the customer's voice saying whatever they said.
/// </para>
/// </summary>
public sealed class TelRecordingAccessLog : AggregateRoot
{
    private TelRecordingAccessLog() { }

    public Guid Id { get; private set; }
    public Guid RecordingId { get; private set; }
    public Guid CallId { get; private set; }
    public Guid ActorUserId { get; private set; }

    public TelRecordingAccessKind Kind { get; private set; }

    /// <summary>
    /// Why. Optional for a listen — an agent reviewing their own call needs no essay — and
    /// MANDATORY for an export, which the endpoint enforces rather than this type: the aggregate
    /// is the record of what happened, and refusing to record an export that happened would be
    /// the wrong place to enforce it.
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// The distributed trace id, so an access ties to the HTTP request — and to the other reads of
    /// the same screen — in Seq, without storing anything personal.
    /// </summary>
    public string? CorrelationId { get; private set; }

    public DateTimeOffset AccessedAt { get; private set; }

    public static TelRecordingAccessLog Record(
        Guid tenantId,
        Guid recordingId,
        Guid callId,
        Guid actorUserId,
        TelRecordingAccessKind kind,
        DateTimeOffset accessedAt,
        string? reason = null,
        string? correlationId = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (recordingId == Guid.Empty) throw new DomainException("RecordingId is required.");

        return new TelRecordingAccessLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RecordingId = recordingId,
            CallId = callId,
            ActorUserId = actorUserId,
            Kind = kind,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CorrelationId = correlationId,
            AccessedAt = accessedAt,
        };
    }
}
