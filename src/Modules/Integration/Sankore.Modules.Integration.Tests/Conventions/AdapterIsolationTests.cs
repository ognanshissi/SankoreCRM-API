namespace Sankore.Modules.Integration.Tests.Conventions;

using System.Xml.Linq;
using FluentAssertions;
using Sankore.Modules.Integration;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-02, last criterion: <b>no other module may reference an <c>Adapters.*</c> project</b>.
/// ASS-02 repeats it for the insurance adapters.
///
/// <para>
/// This repo has no NetArchTest and no ArchUnit (checked), and an assembly-level check would be
/// the wrong instrument anyway: by the time a reference shows up in compiled metadata it is
/// already in the build graph. The rule is about <c>&lt;ProjectReference&gt;</c> lines, so the
/// test reads the <c>.csproj</c> files — the rule at its source, and it fails on the commit that
/// adds the line rather than on the one that first uses it.
/// </para>
///
/// <para>
/// Why the rule exists: the IMF chooses its core banking system, and the same SANKORE deployment
/// serves a Temenos tenant and a batch-file tenant. A module that could reference an adapter
/// would inevitably grow a branch on which one, and that branch is the thing
/// <c>ICoreBankingGateway</c> exists to make impossible.
/// </para>
/// </summary>
public sealed class AdapterIsolationTests
{
    private const string AdaptersMarker = "Sankore.Modules.Integration.Adapters.";

    /// <summary>
    /// The two projects allowed to name an adapter: the adapter's own test project, and the
    /// bootstrapper, which is the only place in the solution permitted to compose modules.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "Sankore.Modules.Integration.Tests",
        "Sankore.Api",
    ];

    [Fact]
    public void No_project_outside_the_bootstrapper_and_the_module_tests_references_an_adapter()
    {
        var offenders = new List<string>();

        foreach (var csproj in EnumerateProjects())
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);

            // An adapter may reference its own siblings; the rule is about everyone else.
            if (projectName.StartsWith(AdaptersMarker, StringComparison.Ordinal)) continue;
            if (Allowed.Contains(projectName, StringComparer.Ordinal)) continue;

            foreach (var reference in ProjectReferencesOf(csproj))
            {
                if (reference.Contains(AdaptersMarker, StringComparison.Ordinal))
                    offenders.Add($"{projectName} → {Path.GetFileNameWithoutExtension(reference)}");
            }
        }

        offenders.Should().BeEmpty(
            "a consumer that can name an adapter will eventually branch on which CBS the tenant "
            + "runs, which is exactly what the gateway contract exists to prevent; depend on "
            + "Sankore.Modules.Integration.PublicApi instead");
    }

    [Fact]
    public void The_adapter_projects_reference_the_module_and_not_another_module()
    {
        var adapters = EnumerateProjects()
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .StartsWith(AdaptersMarker, StringComparison.Ordinal))
            .ToList();

        adapters.Should().NotBeEmpty("the Fake adapter project is part of the socle (INT-10)");

        foreach (var adapter in adapters)
        {
            var referenced = ProjectReferencesOf(adapter)
                .Select(Path.GetFileNameWithoutExtension)
                .ToList();

            // An adapter implements THIS module's ports. Reaching into another module's main
            // assembly would put a second module's domain behind a wire format we do not own.
            referenced.Should().OnlyContain(
                name => name!.StartsWith("Sankore.Modules.Integration", StringComparison.Ordinal)
                        || name.StartsWith("Sankore.Shared.", StringComparison.Ordinal),
                $"{Path.GetFileNameWithoutExtension(adapter)} may only reach the integration "
                + "module and the shared projects");
        }
    }

    [Fact]
    public void The_module_references_no_other_modules_main_assembly()
    {
        var module = EnumerateProjects().Single(p =>
            Path.GetFileNameWithoutExtension(p) == "Sankore.Modules.Integration");

        var crossModule = ProjectReferencesOf(module)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name!.StartsWith("Sankore.Modules.", StringComparison.Ordinal))
            .Where(name => !name!.StartsWith("Sankore.Modules.Integration", StringComparison.Ordinal))
            .ToList();

        crossModule.Should().OnlyContain(
            name => name!.EndsWith(".PublicApi", StringComparison.Ordinal),
            "cross-module dependencies are PublicApi contracts only, never a main assembly");
    }

    // ── ASS-02, last criterion ──────────────────────────────────────────────
    //
    // « Un test d'architecture interdit toute référence directe à un adaptateur assurance. »
    // Extended here rather than given a second mechanism: the project-reference rule above is
    // already family-agnostic, so what the insurance criterion actually needs is proof that an
    // insurance adapter FALLS UNDER it — plus the two leaks the reference rule cannot see.

    /// <summary>
    /// Every insurance adapter is named so the rule above catches it.
    ///
    /// <para>
    /// This looks tautological and is not: the rule is a string match on
    /// <c>Sankore.Modules.Integration.Adapters.</c>, so an ORASS adapter shipped as
    /// <c>Sankore.Integration.Orass</c> — the naming the relay agent project already uses in this
    /// repo, which makes the mistake plausible rather than theoretical — would be referenced by
    /// any module with the architecture test still green. What is pinned is the premise the rule
    /// depends on, for every kind that can be an insurer.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_insurance_adapter_project_is_named_so_the_reference_rule_covers_it()
    {
        // Orass today; a second insurer added to the enum lands here with no edit.
        var insuranceKinds = new[] { IntegrationKind.Orass };

        var adapterProjects = EnumerateProjects()
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();

        foreach (var kind in insuranceKinds)
        {
            // An adapter for this kind may not be shipped yet (ASS-06 depends on the ORSYS
            // specification). When it is, it must sit under the marker.
            var projects = adapterProjects
                .Where(p => p.EndsWith($".{kind}", StringComparison.Ordinal))
                .ToList();

            projects.Should().OnlyContain(
                p => p.StartsWith(AdaptersMarker, StringComparison.Ordinal),
                $"an adapter for {kind} named outside {AdaptersMarker}* escapes the project-"
                + "reference rule entirely, and a consumer could reference it with every "
                + "architecture test still passing");
        }
    }

    /// <summary>
    /// The MODULE is not its own insurance adapter.
    ///
    /// <para>
    /// The reference rule reads <c>.csproj</c> files, so it is blind to the one way an adapter can
    /// reach a consumer without any reference at all: the module implementing the ports itself.
    /// That would make <c>Sankore.Modules.Integration</c> — which every consumer's PublicApi
    /// dependency transitively sits next to, and which the bootstrapper references directly —
    /// carry insurer-specific wire code, and the gateway contract would no longer be the only way
    /// in. Reflection and not project files, because this leak has no project file.
    /// </para>
    /// </summary>
    [Fact]
    public void The_module_assembly_implements_no_insurance_port()
    {
        var ports = new[]
        {
            typeof(IInsuranceProductPort),
            typeof(IInsurancePolicyPort),
            typeof(IInsuranceClaimPort),
        };

        var offenders = typeof(IntegrationModule).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => ports.Any(p => p.IsAssignableFrom(t)))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "the insurance ports are implemented by adapter assemblies the host chooses to ship; "
            + "an implementation inside the module would put one insurer's wire format in the "
            + "assembly every deployment loads");
    }

    /// <summary>
    /// The contract assembly pulls in nothing but the zero-dependency kernel.
    ///
    /// <para>
    /// This is the teeth of "a consumer never depends on an adapter". A consumer module references
    /// <c>Sankore.Modules.Integration.PublicApi</c> and inherits whatever THAT project references,
    /// so a reference added here would be acquired transitively by every consumer and the
    /// direct-reference rule above would become decorative.
    /// </para>
    ///
    /// <para>
    /// <c>Sankore.Shared.Kernel</c> is the one permitted dependency — it is the repo's
    /// zero-dependency project, and the contract needs it for <c>Result</c> and the shared value
    /// objects. Allowing exactly it, by name, rather than asserting an empty list: the point is
    /// that nothing MODULE-shaped or ADAPTER-shaped can get in, and an empty-list assertion would
    /// fail on a legitimate kernel reference while still passing if the kernel itself grew a
    /// dependency.
    /// </para>
    /// </summary>
    [Fact]
    public void The_insurance_contract_assembly_reaches_no_module_and_no_adapter()
    {
        var publicApi = EnumerateProjects().Single(p =>
            Path.GetFileNameWithoutExtension(p) == "Sankore.Modules.Integration.PublicApi");

        ProjectReferencesOf(publicApi)
            .Select(Path.GetFileNameWithoutExtension)
            .Should().OnlyContain(
                name => name == "Sankore.Shared.Kernel",
                "the gateway contract is what every consumer module depends on, so anything it "
                + "references is inherited by all of them; only the zero-dependency kernel may "
                + "travel that way");

        // And the kernel it leans on must itself stay dependency-free, or the guarantee above is
        // one hop deep.
        var kernel = EnumerateProjects().Single(p =>
            Path.GetFileNameWithoutExtension(p) == "Sankore.Shared.Kernel");

        ProjectReferencesOf(kernel).Should().BeEmpty(
            "Sankore.Shared.Kernel is declared to have zero dependencies, and the contract's "
            + "isolation rests on that");
    }

    /// <summary>
    /// Walks up to the repository root — found by the solution file — rather than assuming a
    /// relative depth from the test binary, which changes with the target framework folder.
    /// </summary>
    private static IEnumerable<string> EnumerateProjects()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SankoreCRM.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find the repository root");

        return Directory.EnumerateFiles(dir!.FullName, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));
    }

    private static IEnumerable<string> ProjectReferencesOf(string csprojPath)
        => XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Replace('\\', Path.DirectorySeparatorChar));
}
