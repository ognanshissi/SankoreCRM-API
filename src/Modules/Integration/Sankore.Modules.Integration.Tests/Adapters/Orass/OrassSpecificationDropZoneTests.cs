namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using Xunit;

/// <summary>
/// ASS-06, criteria 1 and 4 — « l'adaptateur implémente les ports assurance sur l'API externe ou le
/// module Bancassurance d'ORASS, selon la spécification fournie par ORSYS ou l'assureur » and
/// « tests de contrat verts sur l'environnement de test de l'assureur ». <b>Neither is satisfiable:
/// there is no specification, no insurer agreement and no test environment.</b> What is deliverable
/// is the drop zone that will receive them, and a tripwire that fires the day one arrives.
///
/// <para>
/// Why a test and not a note in a backlog. An artefact dropped into a repository by someone who
/// finally got it out of a partner is invisible: it does not break a build, nobody is told, and the
/// mapping work it unblocks waits for whoever next reads the folder. This test makes the arrival
/// loud — it fails, once, with the instructions for what to write — which is the only notification
/// channel in a repository that cannot be ignored.
/// </para>
///
/// <para>
/// The folder is located by walking up to the solution file, the same way
/// <c>AdapterIsolationTests</c> finds the repository root, rather than by copying content into the
/// test output. A bordereau sample must be read exactly as supplied, and a copy step is a place
/// where an encoding gets normalised — which would destroy the one fact a parser test needs, since
/// a West-African insurer's back-office export is rarely UTF-8.
/// </para>
/// </summary>
public sealed class OrassSpecificationDropZoneTests
{
    [Fact]
    public void The_drop_zone_exists_and_documents_the_two_separate_asks()
    {
        var readme = Path.Combine(SpecificationDirectory(), "README.md");

        File.Exists(readme).Should().BeTrue(
            "criteria 1 and 4 cannot be met, so the deliverable is a harness somebody can actually "
            + "use: the folder, and a note saying which artefacts to ask ORSYS and the insurer for");

        var content = File.ReadAllText(readme);

        // The two asks are named separately, because they concern two carriers at two different
        // insurers and an answer to one unblocks nothing about the other.
        content.Should().Contain("api-catalogue");
        content.Should().Contain("bordereau-layout");

        // The acknowledgement among them: the artefact whose absence leaves a subscription open
        // forever, and whose meaning (received vs issued) is the difference between a customer
        // being told they are covered and actually being covered.
        content.Should().Contain("inbound.ack");

        // The two counterparties, which is what distinguishes this prerequisite from the three
        // core-banking ones: a publisher cannot grant a partner's permission.
        content.Should().Contain("agreement");

        // The two guards on the artefacts themselves.
        content.Should().Contain("both branches");
        content.Should().Contain("No credential, ever");
        content.Should().Contain("No real policyholder data may be committed");
    }

    [Fact]
    public void No_artefact_has_arrived_yet_and_the_day_one_does_this_test_says_what_to_write()
    {
        var present = Directory
            .EnumerateFiles(SpecificationDirectory())
            .Select(path => Path.GetFileName(path)!)
            .Where(name => !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

        present.Should().BeEmpty(
            "an ORASS artefact is present, so ASS-06 has just become actionable and this test is "
            + "the notification. Next steps, in order, and they are spelled out in this folder's "
            + "README: (1) identify which carrier it describes — an API catalogue and a bordereau "
            + "layout unblock different halves; (2) generate the client if it is an OpenAPI "
            + "document, the way M02's biometry client is generated — never hand-write wire "
            + "records; (3) put the intermediary code and the branch on every submission before "
            + "any field mapping exists; (4) make CheckHealthAsync VERIFY the (branch, code) pair "
            + "against the insurer, not merely require it, and keep activation closed until it "
            + "does; (5) read the credential per request from the vault key OrassCredential "
            + "already probes; (6) on the bordereau carrier, REPLACE DelimitedOutboundBatchFormatter "
            + "— the dispatcher routes a Batch connection before resolving an adapter, so removing "
            + "this adapter's refusals does nothing there; (7) remove the refusals one method at a "
            + "time, leaving the capability matrix, the carrier routing, the credential probe, the "
            + "registration and the intermediary guard untouched. Artefacts found: "
            + string.Join(", ", present));
    }

    /// <summary>
    /// The folder next to this test file, found through the solution root so that the path does not
    /// depend on the target-framework folder the test binary happens to sit in.
    /// </summary>
    private static string SpecificationDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find the repository root");

        var path = Path.Combine(
            dir!.FullName,
            "src", "Modules", "Integration", "Sankore.Modules.Integration.Tests",
            "Adapters", "Orass", "Specification");

        Directory.Exists(path).Should().BeTrue($"the drop zone is part of the deliverable: {path}");

        return path;
    }
}
