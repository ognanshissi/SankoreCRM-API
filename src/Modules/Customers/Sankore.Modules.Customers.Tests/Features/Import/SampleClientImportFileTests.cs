namespace Sankore.Modules.Customers.Tests.Features.Import;

using FluentAssertions;
using Sankore.Modules.Customers.Features.Import.Readers;
using Sankore.Modules.Customers.Features.Import.ValidateImport;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Keeps <c>docs/sample-client-import.xlsx</c> honest. A sample file is the first thing an
/// operator opens, and one that silently stops matching the template — a renamed column, a new
/// mandatory field — is worse than no sample at all: it teaches the wrong format.
/// </summary>
public sealed class SampleClientImportFileTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory = new(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    private sealed class DiskFileStore(string path) : IFileStore
    {
        public Task<string> StoreAsync(Stream c, string n, CancellationToken ct) => Task.FromResult(path);
        public Task<Stream> ReadAsync(string r, CancellationToken ct)
            => Task.FromResult<Stream>(File.OpenRead(path));
        public Task DeleteAsync(string r, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_shipped_sample_file_still_matches_the_import_template()
    {
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "SankoreCRM.sln")))
            repoRoot = repoRoot.Parent;

        repoRoot.Should().NotBeNull("the test runs from inside the repository");

        var path = Path.Combine(repoRoot!.FullName, "docs", "sample-client-import.xlsx");
        File.Exists(path).Should().BeTrue($"the sample file is expected at {path}");

        var reader = new ClientFileImportReader(new DiskFileStore(path));
        var rows = await reader.ReadAsync(path, CancellationToken.None);
        rows.Should().HaveCount(100);
        rows.Count(r => r.IsLegalEntity).Should().Be(8, "the sample covers both client types");

        await using var db = _factory.CreateContext();
        var codes = new[] { "SARL", "SA", "SAS", "SCOOPS", "GIE", "Association", "SNC", "SCI", "ONG" };
        db.LegalForms.AddRange(codes.Select((c, i) =>
            Sankore.Modules.Customers.Domain.LegalForm.Create(_tenantId, c, c, (i + 1) * 10)));
        await db.SaveChangesAsync();

        var validator = new ClientImportValidator(db, TestDoubles.Indexer(), TestDoubles.Settings(_tenantId));
        var report = await validator.ValidateAsync(rows, _tenantId, CancellationToken.None);

        var offending = report.Rows.Where(r => !r.IsValid)
            .Select(r => $"row {r.RowNumber} ({r.DisplayName}): {string.Join(" | ", r.Errors)}")
            .ToList();

        offending.Should().BeEmpty();
        report.ValidRows.Should().Be(100);
    }
}
