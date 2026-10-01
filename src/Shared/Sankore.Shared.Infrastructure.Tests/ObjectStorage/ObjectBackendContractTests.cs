namespace Sankore.Shared.Infrastructure.Tests.ObjectStorage;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// One suite, run against every backend.
///
/// <para>
/// It exists because the backends are substitutable by design: the KYC store is written against
/// <see cref="IObjectBackend"/> and tested against the filesystem, the handlers above it are
/// tested against the in-memory one, and the migration reads from one and writes to the other.
/// The moment those three disagree about what "absent", "too large" or "already used" means, the
/// tests that use the cheap backend stop saying anything about production — and nothing would
/// report it. So the contract is asserted once and inherited, not restated per implementation.
/// </para>
/// </summary>
public abstract class ObjectBackendContractTests
{
    protected abstract IObjectBackend CreateBackend();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private const long Unbounded = long.MaxValue;

    [Fact]
    public async Task Put_then_get_should_return_the_same_bytes()
    {
        var sut = CreateBackend();
        var content = Bytes("évidence");

        await sut.PutAsync("tenant/ab/object.bin", content);

        (await sut.GetAsync("tenant/ab/object.bin", Unbounded)).Should().Equal(content);
    }

    [Fact]
    public async Task An_unknown_key_should_be_absent_rather_than_an_error()
    {
        var sut = CreateBackend();

        (await sut.GetAsync("tenant/ab/never-written.bin", Unbounded)).Should().BeNull();
        (await sut.DeleteAsync("tenant/ab/never-written.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_should_report_what_it_removed_and_stay_idempotent()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/b.bin", Bytes("x"));

        (await sut.DeleteAsync("a/b.bin")).Should().BeTrue();
        (await sut.GetAsync("a/b.bin", Unbounded)).Should().BeNull();
        (await sut.DeleteAsync("a/b.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task An_object_beyond_the_caller_ceiling_should_be_refused_not_truncated()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/big.bin", new byte[4096]);

        (await sut.GetAsync("a/big.bin", 1024)).Should().BeNull();
        (await sut.GetAsync("a/big.bin", 4096)).Should().HaveCount(4096, "the limit is inclusive");
    }

    [Fact]
    public async Task A_malformed_key_should_be_absent_on_read_and_refused_on_write()
    {
        var sut = CreateBackend();

        foreach (var key in new[] { "", "   ", "/etc/passwd", "../../etc/passwd", "a/../../b", "a\n/b" })
        {
            (await sut.GetAsync(key, Unbounded)).Should().BeNull($"'{key}' is not a key");
            (await sut.DeleteAsync(key)).Should().BeFalse($"'{key}' is not a key");

            var write = () => sut.PutAsync(key, Bytes("x"));
            await write.Should().ThrowAsync<ArgumentException>(
                "a bad key on the WRITE path is the caller's own bug and must surface as one");
        }
    }

    [Fact]
    public async Task Reusing_a_key_should_be_refused_rather_than_silently_overwrite()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/once.bin", Bytes("first"));

        var act = () => sut.PutAsync("a/once.bin", Bytes("second"));

        await act.Should().ThrowAsync<Exception>();
        (await sut.GetAsync("a/once.bin", Unbounded)).Should().Equal(Bytes("first"),
            "evidence a decision was taken on must never be replaced in place");
    }

    [Fact]
    public async Task List_should_return_the_keys_that_were_written_under_a_prefix()
    {
        var sut = CreateBackend();
        await sut.PutAsync("t1/ab/one.bin", Bytes("1"));
        await sut.PutAsync("t1/cd/two.bin", Bytes("2"));
        await sut.PutAsync("t2/ef/three.bin", Bytes("3"));

        var all = await Collect(sut.ListAsync(string.Empty));
        var scoped = await Collect(sut.ListAsync("t1/"));

        all.Should().BeEquivalentTo(["t1/ab/one.bin", "t1/cd/two.bin", "t2/ef/three.bin"]);
        scoped.Should().BeEquivalentTo(["t1/ab/one.bin", "t1/cd/two.bin"],
            "a key prefix is the only tenant partition a bucket has");

        // The migration feeds each listed key straight back to GetAsync; a key that lists but does
        // not resolve would be a document silently left behind.
        foreach (var key in all) (await sut.GetAsync(key, Unbounded)).Should().NotBeNull();
    }

    [Fact]
    public async Task List_should_use_forward_slashes_whatever_the_platform()
    {
        var sut = CreateBackend();
        await sut.PutAsync("t1/ab/one.bin", Bytes("1"));

        (await Collect(sut.ListAsync(string.Empty))).Single().Should().Be("t1/ab/one.bin");
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> keys)
    {
        var result = new List<string>();
        await foreach (var key in keys) result.Add(key);
        return result;
    }
}

public sealed class InMemoryObjectBackendTests : ObjectBackendContractTests
{
    protected override IObjectBackend CreateBackend() => new InMemoryObjectBackend();
}

public sealed class LocalObjectBackendTests : ObjectBackendContractTests, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sankore-objects-tests", Guid.NewGuid().ToString("N"));

    protected override IObjectBackend CreateBackend() =>
        new LocalObjectBackend(_root, NullLogger<LocalObjectBackend>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The property the shared key rule exists for, asserted where it can actually be violated:
    /// a file next to the root, which a traversal would reach and which must come through every
    /// operation untouched. This is the latent hole <c>LocalFileStore.ReadAsync</c> has today —
    /// <c>Path.Combine(BasePath, fileReference)</c> with no check — closed here once for every
    /// caller rather than per store.
    /// </summary>
    [Fact]
    public async Task A_crafted_key_should_not_reach_a_file_outside_the_root()
    {
        var sut = CreateBackend();
        var outside = Path.Combine(Path.GetTempPath(), $"sankore-outside-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(outside, "not yours");

        try
        {
            string[] escapes =
            [
                "../" + Path.GetFileName(outside),
                "../../../../../../../../etc/passwd",
                "a/../../" + Path.GetFileName(outside),
                Path.GetFullPath(outside),
            ];

            foreach (var escape in escapes)
            {
                (await sut.GetAsync(escape, long.MaxValue)).Should().BeNull($"'{escape}' escapes the root");
                (await sut.DeleteAsync(escape)).Should().BeFalse($"'{escape}' escapes the root");
            }

            File.Exists(outside).Should().BeTrue("delete must not reach outside either");
            (await File.ReadAllTextAsync(outside)).Should().Be("not yours");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_key_collision_should_not_leave_a_temporary_file_on_the_volume()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/once.bin", [1, 2, 3]);

        var act = () => sut.PutAsync("a/once.bin", [4, 5, 6]);
        await act.Should().ThrowAsync<IOException>();

        Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty(
            "a retried bug would otherwise fill the volume with half-written objects");
    }

    [Fact]
    public async Task An_interrupted_write_should_not_be_listed_as_an_object()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/good.bin", [1]);

        // What a crash between the write and the move leaves behind.
        await File.WriteAllBytesAsync(Path.Combine(_root, "a", "orphan.bin.tmp"), [9]);

        var keys = new List<string>();
        await foreach (var key in sut.ListAsync(string.Empty)) keys.Add(key);

        keys.Should().Equal("a/good.bin");
    }

    [Fact]
    public async Task An_oversized_object_should_be_refused_without_being_read_into_memory()
    {
        var sut = CreateBackend();
        await sut.PutAsync("a/big.bin", new byte[8 * 1024 * 1024]);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        (await sut.GetAsync("a/big.bin", 1024)).Should().BeNull();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        allocated.Should().BeLessThan(1024 * 1024,
            "the ceiling must be decided from the length, not after buffering the object");
    }
}
