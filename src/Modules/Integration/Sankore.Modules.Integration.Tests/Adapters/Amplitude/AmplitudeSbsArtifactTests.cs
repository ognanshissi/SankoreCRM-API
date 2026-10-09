namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using FluentAssertions;
using Xunit;

/// <summary>
/// INT-31, criterion 4 — « tests de contrat verts sur l'environnement de test fourni par l'IMF ou
/// par SBS ». <b>Not satisfiable: there is no contract, no access and no environment.</b> What is
/// deliverable is the drop zone that will receive them, and a tripwire that fires the day one
/// arrives.
///
/// <para>
/// Why a test and not a note in a backlog. An artefact dropped into a repository by someone who
/// finally got it out of a supplier is invisible: it does not break a build, nobody is told, and
/// the mapping work it unblocks waits for whoever next reads the folder. This test makes the
/// arrival loud — it fails, once, with the instructions for what to write — which is the only
/// notification channel in a repository that cannot be ignored.
/// </para>
///
/// <para>
/// The folder is located by walking up to the solution file, the same way
/// <c>AdapterIsolationTests</c> finds the repository root, rather than by copying content into the
/// test output. A batch sample must be read exactly as supplied, and a copy step is a place where
/// an encoding gets normalised — which would destroy the one fact a parser test needs, since a
/// West-African Amplitude installation is rarely UTF-8.
/// </para>
/// </summary>
public sealed class AmplitudeSbsArtifactTests
{
    [Fact]
    public void The_drop_zone_exists_and_documents_the_two_separate_asks()
    {
        var readme = Path.Combine(ArtifactDirectory(), "README.md");

        File.Exists(readme).Should().BeTrue(
            "criterion 4 cannot be met, so the deliverable is a harness somebody can actually "
            + "use: the folder, and a note saying which artefacts to ask SBS for");

        var content = File.ReadAllText(readme);

        // The two asks are named separately, because they are two conversations with two teams
        // about two releases, and an answer to one unblocks nothing about the other.
        content.Should().Contain("api-catalogue");
        content.Should().Contain("file-layout");

        // The acknowledgement among them: the artefact whose absence leaves a command open
        // forever. And the warning against committing an institution's real portfolio.
        content.Should().Contain("inbound.ack");
        content.Should().Contain("must not be committed");
    }

    [Fact]
    public void No_artefact_has_arrived_yet_and_the_day_one_does_this_test_says_what_to_write()
    {
        var present = Directory
            .EnumerateFiles(ArtifactDirectory())
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        present.Should().BeEmpty(
            "an Amplitude artefact is present, so INT-31 criterion 4 has just become actionable "
            + "and this test is the notification. Next steps, in order: (1) identify which release "
            + "the artefact describes — an Up catalogue and a pre-Up file layout unblock different "
            + "halves of this adapter and nothing else; (2) for Up, prefer GENERATING the client "
            + "from the document (M02's biometry client is the pattern) over hand-writing records, "
            + "and for pre-Up write one mapping type per record from the layout, with no guessed "
            + "field names; (3) replace this test with a contract assertion over each artefact in "
            + "this folder; (4) remove the matching refusal from AmplitudeAdapter, one method at a "
            + "time, leaving AmplitudeCapabilityMatrix, AmplitudeCarrierRouting and the "
            + "registration untouched — those three are already correct and do not depend on the "
            + "document. Artefacts found: " + string.Join(", ", present));
    }

    /// <summary>
    /// The folder next to this test file, found through the solution root so that the path does
    /// not depend on the target-framework folder the test binary happens to sit in.
    /// </summary>
    private static string ArtifactDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find the repository root");

        var path = Path.Combine(
            dir!.FullName,
            "src", "Modules", "Integration", "Sankore.Modules.Integration.Tests",
            "Adapters", "Amplitude", "SbsArtifacts");

        Directory.Exists(path).Should().BeTrue($"the drop zone is part of the deliverable: {path}");

        return path;
    }
}
