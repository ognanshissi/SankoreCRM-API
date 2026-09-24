namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Text;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Sdk;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class SdkVersionTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly string _tempDir;
    private readonly ISdkFileStore _fileStore;

    public SdkVersionTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _tempDir = Path.Combine(Path.GetTempPath(), $"sdk-test-{Guid.NewGuid()}");
        _fileStore = new LocalSdkFileStore(_tempDir);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    // ── Domain ──────────────────────────────────────────────────────────

    [Fact]
    public void Publish_creates_version_with_IsCurrent_true()
    {
        var v = SdkVersion.Publish("1.0.0", 1, "sha384-abc");
        v.Version.Should().Be("1.0.0");
        v.Major.Should().Be(1);
        v.SriHash.Should().Be("sha384-abc");
        v.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Revoke_sets_IsCurrent_to_false()
    {
        var v = SdkVersion.Publish("1.0.0", 1, "sha384-abc");
        v.Revoke();
        v.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void Publish_rejects_empty_version()
    {
        var act = () => SdkVersion.Publish("", 1, "sha384-abc");
        act.Should().Throw<DomainException>();
    }

    // ── File store ──────────────────────────────────────────────────────

    [Fact]
    public async Task LocalSdkFileStore_write_and_read_round_trips()
    {
        var content = Encoding.UTF8.GetBytes("console.log('hello');");
        await _fileStore.WriteAsync("1.0.0", "forms.min.js", content, CancellationToken.None);

        var read = await _fileStore.ReadAsync("1.0.0", "forms.min.js", CancellationToken.None);
        read.Should().Equal(content);
    }

    [Fact]
    public async Task LocalSdkFileStore_read_returns_null_for_missing()
    {
        var read = await _fileStore.ReadAsync("9.9.9", "forms.min.js", CancellationToken.None);
        read.Should().BeNull();
    }

    // ── Handler ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_computes_sri_and_marks_current()
    {
        await using var db = _factory.CreateContext();
        var handler = new PublishSdkVersionHandler(db, _fileStore);

        var content = Encoding.UTF8.GetBytes("var x = 1;");
        var result = await handler.Handle(
            new PublishSdkVersionCommand("1.0.0", 1, content), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SriHash.Should().StartWith("sha384-");
        result.Value.Version.Should().Be("1.0.0");

        await using var verify = _factory.CreateContext();
        var version = verify.SdkVersions.Single(v => v.Id == result.Value.Id);
        version.IsCurrent.Should().BeTrue();

        // File was written
        _fileStore.Exists("1.0.0", "forms.min.js").Should().BeTrue();
    }

    [Fact]
    public async Task Publish_revokes_previous_current_for_same_major()
    {
        // Seed v1.0.0
        await using var seedDb = _factory.CreateContext();
        var v1 = SdkVersion.Publish("1.0.0", 1, "sha384-old");
        seedDb.SdkVersions.Add(v1);
        await seedDb.SaveChangesAsync();
        await _fileStore.WriteAsync("1.0.0", "forms.min.js", "v1"u8.ToArray(), CancellationToken.None);

        // Publish v1.1.0 — should revoke v1.0.0
        await using var db = _factory.CreateContext();
        var handler = new PublishSdkVersionHandler(db, _fileStore);
        var result = await handler.Handle(
            new PublishSdkVersionCommand("1.1.0", 1, "v1.1"u8.ToArray()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        verify.SdkVersions.Single(v => v.Version == "1.0.0").IsCurrent.Should().BeFalse();
        verify.SdkVersions.Single(v => v.Version == "1.1.0").IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Old_version_file_remains_servable()
    {
        await using var db1 = _factory.CreateContext();
        var handler1 = new PublishSdkVersionHandler(db1, _fileStore);
        await handler1.Handle(
            new PublishSdkVersionCommand("1.0.0", 1, "old-content"u8.ToArray()), CancellationToken.None);

        await using var db2 = _factory.CreateContext();
        var handler2 = new PublishSdkVersionHandler(db2, _fileStore);
        await handler2.Handle(
            new PublishSdkVersionCommand("1.1.0", 1, "new-content"u8.ToArray()), CancellationToken.None);

        // Old file still accessible
        _fileStore.Exists("1.0.0", "forms.min.js").Should().BeTrue();
        var oldContent = await _fileStore.ReadAsync("1.0.0", "forms.min.js", CancellationToken.None);
        Encoding.UTF8.GetString(oldContent!).Should().Be("old-content");
    }

    [Fact]
    public async Task Publish_rejects_duplicate_version()
    {
        await using var db1 = _factory.CreateContext();
        var handler1 = new PublishSdkVersionHandler(db1, _fileStore);
        await handler1.Handle(
            new PublishSdkVersionCommand("1.0.0", 1, "content"u8.ToArray()), CancellationToken.None);

        await using var db2 = _factory.CreateContext();
        var handler2 = new PublishSdkVersionHandler(db2, _fileStore);
        var result = await handler2.Handle(
            new PublishSdkVersionCommand("1.0.0", 1, "other"u8.ToArray()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SDK_VERSION_ALREADY_EXISTS");
    }
}
