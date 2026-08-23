namespace Sankore.Shared.Kernel;

/// <summary>
/// Wraps a paginated list of items with metadata for the client.
/// Page is 1-based. PageSize = 0 means the query returned everything (un-paginated).
/// </summary>
public sealed record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}
