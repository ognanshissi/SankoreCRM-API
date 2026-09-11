namespace Sankore.Shared.Kernel;

public interface ITimestamp
{
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
}