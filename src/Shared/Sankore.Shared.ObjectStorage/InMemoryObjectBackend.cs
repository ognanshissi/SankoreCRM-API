namespace Sankore.Shared.ObjectStorage;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

/// <summary>
/// Backend held in a dictionary, for tests.
///
/// <para>
/// It enforces the same key rule as the real ones rather than accepting anything a test hands it:
/// a substitute that is more permissive than production is how a key that only works in tests
/// gets written, and the test that was supposed to catch it is the one that accepted it.
/// </para>
///
/// <para>
/// It is NOT a cache and must not be registered in a host: every object lives in the process and
/// is gone with it.
/// </para>
/// </summary>
public sealed class InMemoryObjectBackend : IObjectBackend
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>Keys currently held, for a test that asserts on what was written.</summary>
    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    /// <summary>The raw stored bytes, or <c>null</c> — for a test that asserts the object is ciphertext.</summary>
    public byte[]? Peek(string objectKey) => _objects.TryGetValue(objectKey, out var bytes) ? bytes : null;

    /// <summary>Replaces the stored bytes without going through <see cref="PutAsync"/>, to simulate tampering.</summary>
    public void Overwrite(string objectKey, byte[] content) => _objects[objectKey] = content;

    public Task PutAsync(string objectKey, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectKey.Validate(objectKey);

        if (!_objects.TryAdd(objectKey, content))
            throw new InvalidOperationException($"Object key '{objectKey}' is already in use.");

        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken ct = default)
    {
        if (!ObjectKey.IsValid(objectKey)) return Task.FromResult<byte[]?>(null);
        if (!_objects.TryGetValue(objectKey, out var bytes)) return Task.FromResult<byte[]?>(null);

        return Task.FromResult<byte[]?>(bytes.Length > maxBytes ? null : bytes);
    }

    public Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        if (!ObjectKey.IsValid(objectKey)) return Task.FromResult(false);
        return Task.FromResult(_objects.TryRemove(objectKey, out _));
    }

    public async IAsyncEnumerable<string> ListAsync(
        string keyPrefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        keyPrefix ??= string.Empty;

        foreach (var key in _objects.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (key.StartsWith(keyPrefix, StringComparison.Ordinal)) yield return key;
        }

        await Task.CompletedTask;
    }
}
