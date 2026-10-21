namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using Xunit;

/// <summary>
/// The SDK builds that ship with the host (BNF-B-18, and the PO decision that snippets pin the
/// exact version).
///
/// <para>
/// These assert the FILES, not a handler, because the failure they guard against is silent: the
/// seeder reads <c>forms.min.js</c> from each version directory and SKIPS a version whose file it
/// cannot find, and the serving endpoint answers 404 for the same reason. A build sitting in the
/// tree under any other name is invisible to both — registered nowhere, served nowhere, and
/// nothing logs that it was ignored.
/// </para>
/// </summary>
public sealed class ShippedSdkBuildsTests
{
    /// <summary>The name the seeder reads and the endpoint serves. Not a style choice.</summary>
    private const string RequiredFileName = "forms.min.js";

    private static DirectoryInfo SdkRoot()
    {
        // Walk up to the repository root: the test assembly runs from bin/, and the builds live
        // under the API host's wwwroot rather than next to the tests.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "src", "Bootstrapper")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must be able to find the repository root");

        return new DirectoryInfo(Path.Combine(
            directory!.FullName, "src", "Bootstrapper", "Sankore.Api", "wwwroot", "sdk"));
    }

    [Fact]
    public void The_v2_build_ships_and_is_named_what_the_seeder_reads()
    {
        var expected = Path.Combine(SdkRoot().FullName, "1.2.0", RequiredFileName);

        File.Exists(expected).Should().BeTrue(
            $"the capture-contract-v2 build must be at {RequiredFileName}, which is the name "
            + "SdkVersionSeeder reads and SdkServeEndpoint serves");
    }

    [Fact]
    public void The_v2_build_declares_its_own_version()
    {
        // The version a site sees in its payloads, and the one a support engineer reads off a
        // lead. A build whose SDK_VERSION disagrees with its directory is one nobody can trace.
        var content = File.ReadAllText(
            Path.Combine(SdkRoot().FullName, "1.2.0", RequiredFileName));

        content.Should().Contain("var SDK_VERSION = '1.2.0';");
    }

    /// <summary>
    /// Builds known NOT to carry the required file, each with a reason.
    ///
    /// <para>
    /// <b>Empty, and that is the target state.</b> It held <c>1.1.0</c>, whose
    /// <c>forms.min.js</c> had been renamed to <c>forms.js</c>: the seeder never registered it and
    /// <c>/sdk/1.1.0/forms.min.js</c> answered 404, so the build sat in the tree unservable and
    /// unpinnable. It has been renamed back.
    /// </para>
    ///
    /// <para>
    /// Renaming it was safe precisely BECAUSE it was never servable. The rule that a shipped
    /// build is immutable exists so a site pinning a version with <c>integrity</c> does not break
    /// under it — and no site can be pinning bytes that were never served.
    /// </para>
    ///
    /// <para>
    /// <b>This list is meant to stay empty.</b> An entry is a known-broken build, not a
    /// dispensation.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_shipped_build_also_carries_its_readable_source()
    {
        // forms.js est ce qu'un intégrateur lit et ce qu'on corrige ; forms.min.js en est tiré.
        var missing = SdkRoot().EnumerateDirectories()
            .Where(d => !File.Exists(Path.Combine(d.FullName, "forms.js")))
            .Select(d => d.Name)
            .ToList();

        missing.Should().BeEmpty("every wwwroot/sdk/<version>/ must ship forms.js beside forms.min.js");
    }

    [Fact]
    public void The_first_minified_build_is_minified_and_declares_its_version()
    {
        var dir = Path.Combine(SdkRoot().FullName, "1.2.1");
        var source = File.ReadAllText(Path.Combine(dir, "forms.js"));
        var minified = File.ReadAllText(Path.Combine(dir, RequiredFileName));

        source.Should().Contain("var SDK_VERSION = '1.2.1';");
        // Terser inline la constante : la version doit survivre sous la forme qu'envoie le SDK.
        minified.Should().Contain("\"1.2.1\"");
        minified.Length.Should().BeLessThan(source.Length / 2, "forms.min.js must actually be minified");
        minified.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().BeLessThan(5);
    }

    [Fact]
    public void The_WhatsApp_build_is_minified_declares_its_version_and_sends_the_opt_in()
    {
        // 1.3.0 (WA-BE-05): the optional WhatsApp box. The payload key is the contract with
        // WebIngestRequest.WhatsAppConsent, and the definition key the one WebFormResponse serves.
        var dir = Path.Combine(SdkRoot().FullName, "1.3.0");
        var source = File.ReadAllText(Path.Combine(dir, "forms.js"));
        var minified = File.ReadAllText(Path.Combine(dir, RequiredFileName));

        source.Should().Contain("var SDK_VERSION = '1.3.0';");
        minified.Should().Contain("\"1.3.0\"");
        minified.Should().Contain("whatsAppConsent").And.Contain("whatsAppConsentText");
        minified.Length.Should().BeLessThan(source.Length / 2, "forms.min.js must actually be minified");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    public void Builds_published_before_minification_keep_their_exact_bytes(string version)
    {
        // Leur empreinte SRI est épinglée par des sites et enregistrée par le seeder, qui ne la
        // recalcule jamais. forms.js n'en est qu'une copie lisible : les deux doivent rester égaux,
        // et une « minification » de ces fichiers casserait chaque site qui les épingle.
        var dir = Path.Combine(SdkRoot().FullName, version);
        File.ReadAllBytes(Path.Combine(dir, RequiredFileName))
            .Should().Equal(File.ReadAllBytes(Path.Combine(dir, "forms.js")));
    }

    private static readonly string[] KnownUnservable = [];

    [Fact]
    public void Every_shipped_build_carries_the_file_the_seeder_reads()
    {
        // The guard that matters: the seeder skips a version whose forms.min.js it cannot find and
        // the endpoint 404s for the same reason, both silently. A shipped build nobody can serve
        // is worse than one that was never shipped.
        var root = SdkRoot();
        root.Exists.Should().BeTrue();

        // The selector must still match something. A rule over an empty set passes while checking
        // nothing, which is the same trap the architecture tests guard against — and a moved
        // wwwroot would produce exactly that.
        root.EnumerateDirectories().Should().NotBeEmpty(
            "there are shipped SDK builds to check");

        var missing = root.EnumerateDirectories()
            .Where(d => !File.Exists(Path.Combine(d.FullName, RequiredFileName)))
            .Select(d => d.Name)
            .Except(KnownUnservable, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            $"every wwwroot/sdk/<version>/ must contain {RequiredFileName}, or be listed in "
            + $"{nameof(KnownUnservable)} with a reason");
    }

    [Fact]
    public void The_known_unservable_list_still_describes_reality()
    {
        // A stale exception is worse than none: it would keep a rule switched off for a build
        // somebody has since fixed. It fails the day a listed build is repaired, which is the day
        // the entry must come out. The list is empty today, so this guards the next entry.
        var root = SdkRoot();

        foreach (var version in KnownUnservable)
        {
            var directory = Path.Combine(root.FullName, version);

            if (!Directory.Exists(directory))
                continue;

            File.Exists(Path.Combine(directory, RequiredFileName)).Should().BeFalse(
                $"SDK {version} now carries {RequiredFileName}, so remove it from "
                + $"{nameof(KnownUnservable)}");
        }
    }
}
