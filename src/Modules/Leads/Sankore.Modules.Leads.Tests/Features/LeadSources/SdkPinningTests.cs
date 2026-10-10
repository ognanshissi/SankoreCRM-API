namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Sdk;
using Sankore.Modules.Leads.Features.LeadSources.Snippet;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// How an SDK build reaches a site that has already pasted the snippet.
///
/// <para>
/// <b>Why this suite exists.</b> The snippet carried an <c>integrity</c> hash against
/// <c>/sdk/v{major}/forms.min.js</c> — an alias that resolves to whichever build is current. The
/// two cannot both be true: promoting a new build changes the bytes behind a URL whose hash is
/// already live on customers' pages, a browser refuses a script whose integrity fails, and every
/// one of those forms stops loading. The seeder's "never demote" rule was hiding it by never
/// promoting anything, which meant a shipped SDK fix reached nobody instead. Snippets now pin the
/// exact version, which is what makes promotion safe — and these cases pin both halves together,
/// because either one alone is a broken state.
/// </para>
/// </summary>
public sealed class SdkPinningTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly string _tempDir;
    private readonly ISdkFileStore _fileStore;

    public SdkPinningTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _tempDir = Path.Combine(Path.GetTempPath(), $"sdk-pin-{Guid.NewGuid()}");
        _fileStore = new LocalSdkFileStore(_tempDir);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private async Task Ship(string version, string body)
        => await _fileStore.WriteAsync(version, "forms.min.js", Encoding.UTF8.GetBytes(body), default);

    private async Task Seed()
    {
        await using var db = _factory.CreateContext();
        await SdkVersionSeeder.SeedAsync(db, _fileStore, NullLogger.Instance);
    }

    private async Task<List<SdkVersion>> Registered()
    {
        await using var db = _factory.CreateContext();
        return await db.SdkVersions.IgnoreQueryFilters().ToListAsync();
    }

    // ── Promotion ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_highest_shipped_build_in_a_major_becomes_current()
    {
        await Ship("1.0.0", "v1");
        await Ship("1.1.0", "v11");

        await Seed();

        var versions = await Registered();
        versions.Should().HaveCount(2);
        versions.Single(v => v.IsCurrent).Version.Should().Be("1.1.0");
    }

    [Fact]
    public async Task A_newer_build_shipped_later_supersedes_the_one_already_current()
    {
        // The case that mattered: an existing deployment with 1.0.0 current, upgraded to a host
        // that ships 1.1.0. Under "never demote" the new build was registered and then ignored —
        // the alias and every new snippet kept pointing at 1.0.0, so shipping an SDK reached no
        // one. Promotion runs on every start-up precisely so this state repairs itself.
        await Ship("1.0.0", "v1");
        await Seed();
        (await Registered()).Single(v => v.IsCurrent).Version.Should().Be("1.0.0");

        await Ship("1.1.0", "v11");
        await Seed();

        var versions = await Registered();
        versions.Should().HaveCount(2, "1.0.0 stays servable forever at its own URL");
        versions.Single(v => v.IsCurrent).Version.Should().Be("1.1.0");
    }

    [Fact]
    public async Task Exactly_one_build_per_major_is_current()
    {
        await Ship("1.0.0", "v1");
        await Ship("1.1.0", "v11");
        await Ship("2.0.0", "v2");

        await Seed();

        var versions = await Registered();
        versions.Where(v => v.IsCurrent).Select(v => v.Version)
            .Should().BeEquivalentTo(["1.1.0", "2.0.0"]);
    }

    [Fact]
    public async Task Versions_are_ordered_by_semver_and_not_as_strings()
    {
        // "1.10.0" sorts BEFORE "1.9.0" as a string. Ordering by text would leave the older
        // build current and the newer one unreachable.
        await Ship("1.9.0", "v9");
        await Ship("1.10.0", "v10");

        await Seed();

        (await Registered()).Single(v => v.IsCurrent).Version.Should().Be("1.10.0");
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        await Ship("1.0.0", "v1");
        await Ship("1.1.0", "v11");

        await Seed();
        await Seed();

        var versions = await Registered();
        versions.Should().HaveCount(2);
        versions.Count(v => v.IsCurrent).Should().Be(1);
    }

    // ── What the snippet points at ──────────────────────────────────────

    [Fact]
    public async Task The_snippet_pins_the_exact_version_and_its_own_hash()
    {
        await Ship("1.0.0", "v1");
        await Ship("1.1.0", "v11");
        await Seed();

        var snippet = await GetSnippet();

        // The exact version, never the alias: the hash in the attribute is only meaningful
        // against bytes that cannot change.
        snippet.SdkUrl.Should().Be("/sdk/1.1.0/forms.min.js");
        snippet.Html.Should().Contain("/sdk/1.1.0/forms.min.js").And.NotContain("/sdk/v1/");

        var current = (await Registered()).Single(v => v.IsCurrent);
        snippet.SriHash.Should().Be(current.SriHash);
        snippet.Html.Should().Contain($"integrity=\"{current.SriHash}\"");
    }

    [Fact]
    public async Task Promoting_a_build_leaves_an_already_pasted_snippet_alone()
    {
        await Ship("1.0.0", "v1");
        await Seed();
        var pasted = await GetSnippet();

        await Ship("1.1.0", "v11");
        await Seed();

        // The site keeps requesting 1.0.0 with 1.0.0's hash, and that URL still serves exactly
        // the bytes that hash describes. This is the whole point of pinning: the promotion above
        // used to break this form.
        pasted.SdkUrl.Should().Be("/sdk/1.0.0/forms.min.js");
        var served = await _fileStore.ReadAsync("1.0.0", "forms.min.js", default);
        Encoding.UTF8.GetString(served!).Should().Be("v1");

        var fresh = await GetSnippet();
        fresh.SdkUrl.Should().Be("/sdk/1.1.0/forms.min.js");
        fresh.SriHash.Should().NotBe(pasted.SriHash);
    }

    [Fact]
    public async Task The_emailed_snippet_is_the_one_the_screen_shows()
    {
        // It was not: this handler built the mail from SnippetOptions, whose default hash is the
        // literal "sha384-placeholder". A browser refuses a script whose integrity fails, so the
        // snippet we mailed to integrators could never have worked.
        await Ship("1.0.0", "v1");
        await Seed();

        var shown = await GetSnippet();
        var mailed = await CaptureMailedSnippet();

        mailed.Should().Be(shown.Html);
        mailed.Should().NotContain("placeholder");
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private static readonly IOptions<SnippetOptions> FallbackOptions =
        Options.Create(new SnippetOptions());

    private async Task<LeadSourceConfig> SeedSource()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Web Form",
            LeadChannelType.WebForm, 0,
            IntegrationMode.EmbeddedScript,
            settings: new EmbeddedScriptSettings
            {
                Script = new ScriptConfig { FormSelector = "my-form" },
            });
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<SnippetResult> GetSnippet()
    {
        var source = await _factory.CreateContext().LeadSourceConfigs.FirstOrDefaultAsync()
                     ?? await SeedSource();

        await using var db = _factory.CreateContext();
        var handler = new GetSnippetHandler(db, FallbackOptions);
        var result = await handler.Handle(new GetSnippetQuery(source.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    private async Task<string> CaptureMailedSnippet()
    {
        var source = await _factory.CreateContext().LeadSourceConfigs.FirstOrDefaultAsync()
                     ?? await SeedSource();

        var notifications = Substitute.For<INotificationsModule>();
        QueueEmailRequest? captured = null;
        await notifications.QueueEmailAsync(Arg.Do<QueueEmailRequest>(r => captured = r), Arg.Any<CancellationToken>());

        await using var db = _factory.CreateContext();
        var handler = new SendSnippetHandler(db, notifications, FallbackOptions);
        var result = await handler.Handle(
            new SendSnippetCommand(source.Id, "integrator@imf.ci"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        return (string)captured!.TemplateData!["snippet"];
    }
}
