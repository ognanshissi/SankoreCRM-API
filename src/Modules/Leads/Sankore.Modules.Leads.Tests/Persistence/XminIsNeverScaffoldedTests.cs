namespace Sankore.Modules.Leads.Tests.Persistence;

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// No migration in this solution may declare a column named <c>xmin</c>.
///
/// <para>
/// Every module maps its optimistic-concurrency token onto PostgreSQL's <c>xmin</c> SYSTEM column
/// (<c>HasColumnName("xmin").HasColumnType("xid")</c>). That mapping is a read-only binding, but
/// <c>dotnet ef migrations add</c> cannot tell it from a schema change and scaffolds a real column
/// for it — DDL PostgreSQL refuses outright:
/// </para>
/// <code>ERROR: column name "xmin" conflicts with a system column name</code>
///
/// <para>
/// <b>Why a file scan and not a database test.</b> The failure is in generated code nobody reads,
/// and it cannot surface anywhere the suite looks: EF InMemory runs no DDL, so every test passes,
/// and <c>--emit-openapi</c> boots the host without touching a database. What it does instead is
/// abort <c>MigrateAsync</c> at start-up, leaving the table absent — and the first symptom is a
/// swallowed cross-module read answering a code like <c>COMPLIANCE_SETTINGS_UNAVAILABLE</c> on an
/// unrelated endpoint, a very long way from the migration that caused it.
/// </para>
///
/// <para>
/// <b>Renaming the column is not the fix.</b> The CLR property is already called
/// <c>Version</c> — <c>xmin</c> is only where Npgsql stores a rowversion, and the bare
/// <c>IsRowVersion()</c> spelling that M02 and INT use emits that name on its own, with no
/// <c>HasColumnName</c> anywhere. The name is the provider's, not a choice: PostgreSQL maintains
/// <c>xmin</c> and nothing else, so a REAL column called <c>version xid</c> is written once and
/// then never moves (measured: <c>xmin</c> goes 912 → 913 → 914 over two updates while the real
/// column sits at 0). It would compile, scaffold a clean migration, pass this whole suite — and
/// silently retire optimistic concurrency everywhere, since <c>WHERE version = @expected</c> then
/// matches for ever and <c>CONCURRENCY_CONFLICT</c> never fires again. A real column can only be a
/// counter the application increments itself, which is a different mechanism and a different wire
/// type. <c>UseXminAsConcurrencyToken()</c>, which used to hide all of this, was removed in
/// Npgsql 10.
/// </para>
///
/// <para>
/// This is the fourth time the trap was paid for: <c>AddDraftStatusAndXminToLeadSourceConfig</c>
/// (M13), <c>InitialCustomers</c> (M01), <c>AddNotificationSettingsConcurrency</c> (M12) and
/// <c>InitialKyc</c> (M02) each carry a hand-written note about it. Removing the scaffolded line
/// is the fix; this test is what makes forgetting to impossible to ship.
/// </para>
/// </summary>
public sealed class XminIsNeverScaffoldedTests
{
    /// <summary>
    /// Matches both shapes EF scaffolds: <c>xmin = table.Column&lt;uint&gt;(...)</c> inside a
    /// <c>CreateTable</c> initializer, and <c>name: "xmin"</c> on an <c>AddColumn</c>.
    /// </summary>
    private static readonly Regex ScaffoldedXminColumn = new(
        """(^\s*xmin\s*=\s*table\.Column)|(name:\s*"xmin")""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Modules")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must be able to find the repository root");
        return directory!;
    }

    /// <summary>
    /// Every migration body, across every module — the designer files and the model snapshots are
    /// excluded, because those SHOULD carry the mapping. It is only executable DDL that must not.
    /// </summary>
    private static IReadOnlyList<string> MigrationFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot().FullName, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void No_migration_declares_an_xmin_column()
    {
        var root = RepositoryRoot().FullName;

        var offenders = MigrationFiles()
            .Where(path => ScaffoldedXminColumn.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        offenders.Should().BeEmpty(
            "a migration naming \"xmin\" fails on PostgreSQL with 'column name \"xmin\" "
            + "conflicts with a system column name', which aborts MigrateAsync and leaves the "
            + "table absent. Delete the scaffolded line — the mapping belongs to the model "
            + "snapshot, not to the DDL.");
    }

    /// <summary>
    /// The companion case NetArchTest taught this repo to write: a selector that matches nothing
    /// passes every assertion. A renamed migrations folder would turn the rule above green while
    /// checking no files at all.
    /// </summary>
    [Fact]
    public void The_scan_actually_finds_the_migrations()
    {
        var files = MigrationFiles();

        files.Should().HaveCountGreaterThan(
            30, "every module's migrations must be in the scan for the rule above to mean anything");

        files.Should().Contain(
            path => path.EndsWith("_AddComplianceSettings.cs", StringComparison.Ordinal),
            "the migration whose scaffolded xmin column broke the PEP declaration endpoint must "
            + "be one of the files this rule reads");
    }

    /// <summary>
    /// And a case pinning that the pattern recognises what EF actually emits — otherwise a typo in
    /// the regex would make both rules above vacuous.
    /// </summary>
    [Theory]
    [InlineData("""                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)""")]
    [InlineData("""                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),""")]
    [InlineData("""            migrationBuilder.AddColumn<uint>(name: "xmin", schema: "administration",""")]
    public void The_pattern_matches_what_ef_scaffolds(string scaffolded)
        => ScaffoldedXminColumn.IsMatch(scaffolded).Should().BeTrue();
}
