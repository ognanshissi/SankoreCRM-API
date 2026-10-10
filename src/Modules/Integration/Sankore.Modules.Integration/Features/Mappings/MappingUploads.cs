namespace Sankore.Modules.Integration.Features.Mappings;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Shared.Kernel;

/// <summary>
/// The upload half of the two import slices: the file is parked in the
/// <see cref="IFileStore"/> and the handler is given a reference, so a multipart stream never has
/// to survive the MediatR pipeline (it would already be disposed by the time a retry read it).
/// </summary>
internal static class MappingUploads
{
    /// <summary>
    /// A correspondence table is three columns and a few hundred lines; anything much larger is
    /// not a mapping file and should be refused before it reaches storage.
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    internal static async Task<(string? Reference, string? Error)> StoreAsync(
        IFormFile file, IFileStore fileStore, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (null, "The uploaded file is empty.");

        if (file.Length > MaxBytes)
            return (null, $"The file is {file.Length} bytes; the maximum is {MaxBytes} bytes.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != MappingImportReader.Extension)
            return (null, $"Only {MappingImportReader.Extension} files are supported here.");

        await using var stream = file.OpenReadStream();
        return (await fileStore.StoreAsync(stream, file.FileName, ct), null);
    }
}
