namespace Sankore.Shared.Infrastructure.Retention;

using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

public static class TechnicalFiles
{
    /// <summary>
    /// Deletes stored files one by one and returns those that are gone. One that resists does not
    /// stop the others; the caller marks only the returned ones, so the resisting file is retried
    /// on the next run instead of being forgotten on the volume.
    /// </summary>
    public static async Task<IReadOnlySet<string>> DeleteAsync(
        IFileStore store, IEnumerable<string> references, ILogger logger, CancellationToken ct)
    {
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            try
            {
                await store.DeleteAsync(reference, ct);
                deleted.Add(reference);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Technical purge: file {Reference} could not be deleted ({Reason}); retried next run.",
                    reference, ex.GetType().Name);
            }
        }
        return deleted;
    }
}
