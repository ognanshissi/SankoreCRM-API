namespace Sankore.Api.Tests.Features.ObjectStorage;

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Api.Features.ObjectStorage.MigrateObjects;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// What the job does to the volume, and what it leaves behind in the audit trail.
///
/// <para>
/// The property worth a test of its own is that <b>the source survives</b>. The migrator can delete
/// it — that is a supported mode — and the only thing keeping an HTTP-triggered copy from reaching
/// it is one literal argument in this job. The objects behind <c>kyc-documents</c> are identity
/// documents a regulator can demand, so "the endpoint cannot destroy the last copy of one" has to
/// be asserted rather than read.
/// </para>
/// </summary>
public sealed class MigrateObjectsJobTests : IDisposable
{
    private const string Concern = "kyc-documents";

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperatorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _sourceRoot = Path.Combine(
        Path.GetTempPath(), "sankore-migration-job-tests", Guid.NewGuid().ToString("N"));

    private readonly InMemoryObjectBackend _destination = new();
    private readonly RecordingAuditWriter _audit = new();

    public void Dispose()
    {
        if (Directory.Exists(_sourceRoot)) Directory.Delete(_sourceRoot, recursive: true);
    }

    private async Task<string[]> SeedSourceAsync(params string[] keys)
    {
        var backend = new LocalObjectBackend(
            _sourceRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalObjectBackend>.Instance);

        foreach (var key in keys)
            await backend.PutAsync(key, Encoding.UTF8.GetBytes($"ciphertext-of-{key}"));

        return keys;
    }

    private async Task RunJobAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<IObjectBackend>(Concern, _destination);
        services.AddSingleton<IAuditWriter>(_audit);

        var provider = services.BuildServiceProvider();

        await new MigrateObjectsJob(provider.GetRequiredService<IServiceScopeFactory>())
            .ExecuteAsync(Concern, _sourceRoot, TenantId, OperatorId);
    }

    [Fact]
    public async Task Every_object_should_reach_the_destination_and_the_source_must_still_hold_them()
    {
        var keys = await SeedSourceAsync(
            "a1b2c3d4e5f60718/ab/abcdef0123456789abcdef0123456789.kycobj",
            "a1b2c3d4e5f60718/cd/cdef0123456789abcdef0123456789ab.kycobj");

        await RunJobAsync();

        _destination.Keys.Should().BeEquivalentTo(keys);

        foreach (var key in keys)
        {
            File.Exists(Path.Combine(_sourceRoot, key.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue(
                    "an HTTP-triggered migration must never destroy the last copy of KYC evidence — "
                    + "the volume is retired by unmounting it, after a document has been read back");
        }
    }

    [Fact]
    public async Task The_outcome_should_be_audited_with_what_was_actually_copied()
    {
        await SeedSourceAsync("a1b2c3d4e5f60718/ab/abcdef0123456789abcdef0123456789.kycobj");

        await RunJobAsync();

        var entry = _audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("ObjectStorageMigrationCompleted");
        entry.Outcome.Should().Be("SUCCESS");
        entry.ResourceId.Should().Be(Concern);
        entry.UserId.Should().Be(OperatorId, "the outcome row must name the operator, not SYSTEM");

        var payload = JsonDocument.Parse(entry.PayloadJson).RootElement;
        payload.GetProperty("Copied").GetInt32().Should().Be(1);
        payload.GetProperty("Failed").GetInt32().Should().Be(0);
        payload.GetProperty("SourceDeleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_rerun_should_audit_a_resume_rather_than_claim_it_copied_again()
    {
        await SeedSourceAsync("a1b2c3d4e5f60718/ab/abcdef0123456789abcdef0123456789.kycobj");

        await RunJobAsync();
        await RunJobAsync();

        var second = _audit.Entries[^1];
        var payload = JsonDocument.Parse(second.PayloadJson).RootElement;

        payload.GetProperty("Copied").GetInt32().Should().Be(0);
        payload.GetProperty("AlreadyPresent").GetInt32().Should().Be(1);
        second.Outcome.Should().Be("SUCCESS", "a resume that finds everything in place is a success");
    }

    [Fact]
    public async Task An_unreadable_object_should_make_the_audit_row_a_failure_that_names_it()
    {
        await SeedSourceAsync("a1b2c3d4e5f60718/ab/abcdef0123456789abcdef0123456789.kycobj");

        // A file the volume holds that is LISTED but that no Get resolves: its name is legal on
        // the filesystem and is not a legal object key, so ObjectKey refuses it on the way back.
        // That is the realistic shape of a stray — something a backup tool or a human dropped in
        // the folder — and the migrator must report it rather than walk past it, because a file on
        // the evidence volume that cannot be moved is precisely what decides whether the volume
        // may be retired.
        var strayFolder = Path.Combine(_sourceRoot, "a1b2c3d4e5f60718", "ef");
        Directory.CreateDirectory(strayFolder);
        await File.WriteAllTextAsync(Path.Combine(strayFolder, "str*ay.kycobj"), "not an object");

        await RunJobAsync();

        var entry = _audit.Entries.Should().ContainSingle().Which;
        entry.Outcome.Should().Be("FAILURE",
            "an operator must not read 'migrated' over an object that did not move");
        entry.ErrorDetail.Should().Contain("the source was kept");

        JsonDocument.Parse(entry.PayloadJson).RootElement
            .GetProperty("Failures")[0].GetProperty("ObjectKey").GetString()
            .Should().Contain("str*ay.kycobj");
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
