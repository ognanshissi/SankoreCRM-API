namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>Where a batch file stands in its own lifecycle (INT-24/INT-25).</summary>
public enum BatchFileStatus
{
    /// <summary>Written and stored, not yet deposited.</summary>
    Generated,

    /// <summary>Deposited on the SFTP server, directly or through the relay.</summary>
    Sent,

    /// <summary>The external system acknowledged it.</summary>
    Acknowledged,

    /// <summary>Read and applied — for an inbound file.</summary>
    Processed,

    /// <summary>Refused: bad checksum, out-of-order sequence, unreadable.</summary>
    Failed,

    /// <summary>Content deleted after its retention delay; the row stays as evidence.</summary>
    Purged
}

/// <summary>
/// One file exchanged with an external system (INT-24/INT-25).
///
/// <para>
/// The sequence number is unique per connection and direction, and that is not bookkeeping: a
/// CBS that applies files in order must be able to tell a gap from a duplicate, and an inbound
/// file whose sequence has already been processed is ignored rather than applied twice.
/// </para>
///
/// <para>
/// <see cref="ChecksumSha256"/> is over the PLAINTEXT. The stored object is encrypted at rest,
/// so a checksum of the ciphertext would change with every re-encryption and prove nothing about
/// the content the external system received.
/// </para>
/// </summary>
public sealed class IntegrationBatchFile : AggregateRoot
{
    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    public BatchDirection Direction { get; private set; }

    public long SequenceNo { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ChecksumSha256 { get; private set; } = string.Empty;

    public int RecordCount { get; private set; }

    public BatchFileStatus Status { get; private set; }

    /// <summary>
    /// Opaque reference into the object store, where the bytes sit encrypted. Null once purged.
    /// </summary>
    public string? StorageRef { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? AckReceivedAt { get; private set; }

    /// <summary>Why a file failed. Operator-facing; never a line of the file itself.</summary>
    public string? FailureDetail { get; private set; }

    public uint Version { get; private set; }

    private IntegrationBatchFile() { }

    public static IntegrationBatchFile Create(
        Guid tenantId,
        Guid connectionId,
        BatchDirection direction,
        long sequenceNo,
        string fileName,
        string checksumSha256,
        int recordCount,
        TimeProvider clock,
        string? storageRef = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (sequenceNo <= 0) throw new DomainException("A sequence number starts at 1.");
        if (string.IsNullOrWhiteSpace(fileName)) throw new DomainException("A file name is required.");
        if (string.IsNullOrWhiteSpace(checksumSha256))
            throw new DomainException("A checksum is required — an unverifiable file is not evidence.");
        if (recordCount < 0) throw new DomainException("RecordCount cannot be negative.");

        return new IntegrationBatchFile
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Direction = direction,
            SequenceNo = sequenceNo,
            FileName = fileName.Trim(),
            ChecksumSha256 = checksumSha256.Trim().ToLowerInvariant(),
            RecordCount = recordCount,
            Status = BatchFileStatus.Generated,
            StorageRef = storageRef,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    public void MarkSent(TimeProvider clock)
    {
        Status = BatchFileStatus.Sent;
        SentAt = clock.GetUtcNow();
    }

    public void MarkAcknowledged(TimeProvider clock)
    {
        Status = BatchFileStatus.Acknowledged;
        AckReceivedAt = clock.GetUtcNow();
    }

    public void MarkProcessed(TimeProvider clock)
    {
        Status = BatchFileStatus.Processed;
        AckReceivedAt ??= clock.GetUtcNow();
    }

    public void MarkFailed(string detail, TimeProvider clock)
    {
        Status = BatchFileStatus.Failed;
        FailureDetail = detail;
    }

    /// <summary>
    /// Content deleted, row kept. The checksum and the record count survive the purge, which is
    /// what lets an inspection a year later confirm what was sent without keeping the personal
    /// data in it.
    /// </summary>
    public void MarkPurged()
    {
        Status = BatchFileStatus.Purged;
        StorageRef = null;
    }

    /// <summary>True when an acknowledgement is overdue and an alert is owed (INT-25).</summary>
    public bool IsAckOverdue(DateTimeOffset now, int ackTimeoutHours)
        => Direction == BatchDirection.Out
           && Status == BatchFileStatus.Sent
           && SentAt is { } sent
           && now - sent > TimeSpan.FromHours(ackTimeoutHours);
}
