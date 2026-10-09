namespace Sankore.Modules.Integration.Tests.Adapters.PerfectVision;

using FluentAssertions;
using Xunit;

/// <summary>
/// INT-28, criterion 4 — « les tests de contrat passent sur des fichiers d'exemple fournis par
/// l'éditeur ou l'IMF ». <b>Not satisfiable: there are no sample files.</b> What is deliverable is
/// the harness that will consume them, and a tripwire that fires the day they arrive.
///
/// <para>
/// Why a test and not a note in a backlog. A sample file dropped into a repository by someone who
/// finally got it out of a vendor is invisible: it does not break a build, nobody is told, and the
/// mapping work it unblocks waits for whoever next reads the folder. This test makes the arrival
/// loud — it fails, once, with the instructions for what to write — which is the only notification
/// channel in a repository that cannot be ignored.
/// </para>
///
/// <para>
/// The folder is located by walking up to the solution file, the same way
/// <c>AdapterIsolationTests</c> finds the repository root, rather than by copying content into the
/// test output. A sample file must be read exactly as the vendor supplied it, and a copy step is a
/// place where an encoding gets normalised — which would destroy the one fact a parser test needs,
/// since a West-African Perfect Vision installation is rarely UTF-8.
/// </para>
/// </summary>
public sealed class PerfectVisionSampleFileTests
{
    [Fact]
    public void The_drop_zone_exists_and_documents_what_belongs_in_it()
    {
        var readme = Path.Combine(SampleFileDirectory(), "README.md");

        File.Exists(readme).Should().BeTrue(
            "criterion 4 cannot be met, so the deliverable is a harness somebody can actually "
            + "use: the folder, and a note saying which artefacts to ask the vendor for");

        var content = File.ReadAllText(readme);

        // The three things that make the note useful rather than decorative: what files to ask
        // for, the acknowledgement among them (the artefact whose absence leaves a command open
        // forever), and the warning against committing real customer data.
        content.Should().Contain("inbound.ack");
        content.Should().Contain("balance-view.columns");
        content.Should().Contain("must not be committed");
    }

    [Fact]
    public void No_sample_file_has_arrived_yet_and_the_day_one_does_this_test_says_what_to_write()
    {
        var present = Directory
            .EnumerateFiles(SampleFileDirectory())
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        present.Should().BeEmpty(
            "a Perfect Vision sample file is present, so INT-28 criterion 4 has just become "
            + "actionable and this test is the notification. Next steps, in order: (1) write the "
            + "record mapping from the accompanying specification — one type per record, no "
            + "guessed field names; (2) replace this test with a parse assertion over each file "
            + "in this folder; (3) remove the matching refusal from PerfectVisionAdapter, one "
            + "method at a time, leaving the capability matrix and the registration untouched. "
            + "Files found: " + string.Join(", ", present));
    }

    /// <summary>
    /// The folder next to this test file, found through the solution root so that the path does
    /// not depend on the target-framework folder the test binary happens to sit in.
    /// </summary>
    private static string SampleFileDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find the repository root");

        var path = Path.Combine(
            dir!.FullName,
            "src", "Modules", "Integration", "Sankore.Modules.Integration.Tests",
            "Adapters", "PerfectVision", "SampleFiles");

        Directory.Exists(path).Should().BeTrue($"the drop zone is part of the deliverable: {path}");

        return path;
    }
}
