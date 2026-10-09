namespace Sankore.Modules.Integration.Tests.Conventions;

using System.Xml.Linq;
using FluentAssertions;
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
