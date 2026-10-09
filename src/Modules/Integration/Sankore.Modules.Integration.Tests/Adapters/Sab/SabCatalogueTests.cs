namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using FluentAssertions;
using Xunit;

/// <summary>
/// INT-32, criterion 3 — « tests de contrat verts sur l'environnement de test fourni par l'IMF ou
/// par SBS ». <b>Not satisfiable: there is neither a catalogue nor a test environment.</b> What is
/// deliverable is the drop zone that will receive them, and a tripwire that fires the day they
/// arrive.
///
/// <para>
/// Why a test and not a note in a backlog. A document dropped into a repository by someone who
/// finally got it out of SBS is invisible: it does not break a build, nobody is told, and the
/// mapping work it unblocks waits for whoever next reads the folder. This test makes the arrival
/// loud — it fails, once, with the instructions for what to do — which is the only notification
/// channel in a repository that cannot be ignored.
/// </para>
///
/// <para>
/// The folder is located by walking up to the solution file, the same way
/// <c>AdapterIsolationTests</c> finds the repository root, rather than by copying content into the
/// test output: a copy step is a place where an encoding gets normalised, and a vendor document
/// must be read exactly as it was supplied.
/// </para>
/// </summary>
public sealed class SabCatalogueTests
{
    [Fact]
    public void The_drop_zone_exists_and_documents_what_belongs_in_it()
    {
        var readme = Path.Combine(CatalogueDirectory(), "README.md");

        File.Exists(readme).Should().BeTrue(
            "criterion 3 cannot be met, so the deliverable is a harness somebody can actually "
            + "use: the folder, and a note saying which artefacts to ask SBS for");

        var content = File.ReadAllText(readme);

        // The four things that make the note useful rather than decorative: the preferred form of
        // the catalogue (an OpenAPI document, so the client is generated and not hand-written),
        // the entity question, the two-entity requirement without which a green contract suite
        // would prove nothing about the scoping, and the ban on pasting a credential into a file.
        content.Should().Contain("open-sab-openapi.source.json");
        content.Should().Contain("open-sab-entities");
        content.Should().Contain("at least two entities");
        content.Should().Contain("No credential, ever");
    }

    [Fact]
    public void No_catalogue_has_arrived_yet_and_the_day_one_does_this_test_says_what_to_write()
    {
        var present = Directory
            .EnumerateFiles(CatalogueDirectory())
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        present.Should().BeEmpty(
            "an Open SAB document is present, so INT-32 has just become actionable and this test "
            + "is the notification. Next steps, in order, and they are spelled out in this "
            + "folder's README: (1) generate the client if it is an OpenAPI document, the way "
            + "M02's biometry client is generated — never hand-write wire records; (2) put the "
            + "entity on every request before any field mapping exists; (3) make CheckHealthAsync "
            + "VERIFY the entity against the installation, not merely require it, and keep "
            + "activation closed until it does; (4) read the API key per request from the vault "
            + "key SabCredential already probes; (5) remove the refusals one method at a time, "
            + "leaving the capability matrix, the registration and the entity guard untouched. "
            + "Files found: " + string.Join(", ", present));
    }

    private static string CatalogueDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find the repository root");

        var path = Path.Combine(
            dir!.FullName,
            "src", "Modules", "Integration", "Sankore.Modules.Integration.Tests",
            "Adapters", "Sab", "Catalogue");

        Directory.Exists(path).Should().BeTrue($"the drop zone is part of the deliverable: {path}");

        return path;
    }
}
