namespace Sankore.Shared.Kernel;

public abstract record TimestampBase: ITimestamp
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; } = DateTimeOffset.UtcNow;
}