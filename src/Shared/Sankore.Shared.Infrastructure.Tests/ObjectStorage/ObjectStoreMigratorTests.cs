namespace Sankore.Shared.Infrastructure.Tests.ObjectStorage;

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// The migration is the one operation that can destroy evidence, so what is pinned here is not
/// "it copies" but the four ways it must refuse to: it never deletes what it did not verify, it
/// never stops on a single bad object, it never counts a resume as work done, and it never
/// reports a clean run while something failed.
/// </summary>
public sealed class ObjectStoreMigratorTests
{
    private const long Unbounded = long.MaxValue;

    private static ObjectStoreMigrator Sut() => new(NullLogger<ObjectStoreMigrator>.Instance);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static async Task<InMemoryObjectBackend> Seeded(params (string Key, string Content)[] objects)
    {
        var backend = new InMemoryObjectBackend();
        foreach (var (key, content) in objects) await backend.PutAsync(key, Bytes(content));
        return backend;
    }

    [Fact]
    public async Task Every_object_under_the_prefix_should_arrive_byte_identical()
    {
        var source = await Seeded(
            ("kyc/ab/one.kycobj", "ciphertext-1"),
            ("kyc/cd/two.kycobj", "ciphertext-2"));
        var destination = new InMemoryObjectBackend();

        var report = await Sut().MigrateAsync(source, destination, string.Empty, Unbounded);

        report.Copied.Should().Be(2);
        report.IsComplete.Should().BeTrue();
        (await destination.GetAsync("kyc/ab/one.kycobj", Unbounded)).Should().Equal(Bytes("ciphertext-1"));
        (await destination.GetAsync("kyc/cd/two.kycobj", Unbounded)).Should().Equal(Bytes("ciphertext-2"));
    }

    [Fact]
    public async Task Only_the_requested_prefix_should_move()
    {
        var source = await Seeded(("t1/a.bin", "mine"), ("t2/b.bin", "theirs"));
        var destination = new InMemoryObjectBackend();

        var report = await Sut().MigrateAsync(source, destination, "t1/", Unbounded);

        report.Copied.Should().Be(1);
        destination.Keys.Should().Equal("t1/a.bin");
    }

    [Fact]
    public async Task A_copy_should_leave_the_source_untouched_unless_asked_otherwise()
    {
        var source = await Seeded(("a/one.bin", "x"));
        var destination = new InMemoryObjectBackend();

        await Sut().MigrateAsync(source, destination, string.Empty, Unbounded);

        (await source.GetAsync("a/one.bin", Unbounded)).Should().NotBeNull(
            "the default must be a copy: an operator checks the bucket before the volume is wiped");
    }

    [Fact]
    public async Task A_move_should_delete_only_what_it_verified_this_run()
    {
        var source = await Seeded(("a/fresh.bin", "x"), ("a/already-there.bin", "y"));
        var destination = await Seeded(("a/already-there.bin", "y"));

        var report = await Sut().MigrateAsync(
            source, destination, string.Empty, Unbounded, verify: true, deleteFromSource: true);

        report.Copied.Should().Be(1);
        report.AlreadyPresent.Should().Be(1);

        (await source.GetAsync("a/fresh.bin", Unbounded)).Should().BeNull("it was copied and verified");
        (await source.GetAsync("a/already-there.bin", Unbounded)).Should().NotBeNull(
            "a key the destination already held is the resume path, not proof this run copied it");
    }

    [Fact]
    public async Task Re_running_a_finished_migration_should_report_a_resume_not_new_work()
    {
        var source = await Seeded(("a/one.bin", "x"), ("a/two.bin", "y"));
        var destination = new InMemoryObjectBackend();

        await Sut().MigrateAsync(source, destination, string.Empty, Unbounded);
        var second = await Sut().MigrateAsync(source, destination, string.Empty, Unbounded);

        second.Copied.Should().Be(0);
        second.AlreadyPresent.Should().Be(2);
        second.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task One_unreadable_object_should_be_reported_without_stopping_the_others()
    {
        var source = await Seeded(("a/good.bin", "x"), ("a/huge.bin", "yyyyyyyyyy"), ("a/also-good.bin", "z"));
        var destination = new InMemoryObjectBackend();

        // The ceiling makes the middle object unreadable, standing in for a damaged one.
        var report = await Sut().MigrateAsync(source, destination, string.Empty, maxBytes: 5);

        report.Copied.Should().Be(2);
        report.IsComplete.Should().BeFalse();
        report.Failures.Should().ContainSingle()
            .Which.ObjectKey.Should().Be("a/huge.bin");
        destination.Keys.Should().BeEquivalentTo(["a/good.bin", "a/also-good.bin"]);
    }

    [Fact]
    public async Task A_destination_that_corrupts_what_it_stores_should_be_caught_and_the_source_kept()
    {
        var source = await Seeded(("a/one.bin", "ciphertext"));
        var destination = new TruncatingBackend();

        var report = await Sut().MigrateAsync(
            source, destination, string.Empty, Unbounded, verify: true, deleteFromSource: true);

        report.Copied.Should().Be(0);
        report.IsComplete.Should().BeFalse();
        report.Failures.Should().ContainSingle()
            .Which.Reason.Should().Contain("read back");
        (await source.GetAsync("a/one.bin", Unbounded)).Should().NotBeNull(
            "a document whose copy did not verify is the last one that may be deleted");
    }

    /// <summary>Writes one byte less than it was given — a silent truncation, the failure verification exists for.</summary>
    private sealed class TruncatingBackend : IObjectBackend
    {
        private readonly InMemoryObjectBackend _inner = new();

        public Task PutAsync(string objectKey, byte[] content, CancellationToken ct = default)
            => _inner.PutAsync(objectKey, content[..^1], ct);

        public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken ct = default)
            => _inner.GetAsync(objectKey, maxBytes, ct);

        public Task<bool> DeleteAsync(string objectKey, CancellationToken ct = default)
            => _inner.DeleteAsync(objectKey, ct);

        public IAsyncEnumerable<string> ListAsync(string keyPrefix, CancellationToken ct = default)
            => _inner.ListAsync(keyPrefix, ct);
    }
}
