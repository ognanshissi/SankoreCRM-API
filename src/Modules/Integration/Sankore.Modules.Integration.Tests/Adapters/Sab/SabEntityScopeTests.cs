namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Modules.Integration.Domain;
using Xunit;

/// <summary>
/// INT-32, criterion 2 — « il gère l'entité (<c>Entity</c>) pour les réseaux multi-IMF comme le
/// réseau CIF », at the level of the rule itself.
///
/// <para>
/// The rule is pinned on its own, with no adapter, no database and no tenant, because it is the
/// half of INT-32 that does not wait on SBS: the decision is ours, the call is theirs. These tests
/// keep passing unchanged the day the catalogue arrives, which is the point — a scoping rule that
/// had to be re-asserted alongside a new transport is a rule nobody trusts.
/// </para>
/// </summary>
public sealed class SabEntityScopeTests
{
    [Fact]
    public void A_configured_entity_scopes_the_call()
    {
        var settings = SabHarness.ScopedSettings();

        SabEntityScope.ResolveFor(settings).Should().Be(SabEntityResolution.Resolved);
        SabEntityScope.IsScoped(settings).Should().BeTrue();
        SabEntityScope.EntityOf(settings).Should().Be(SabHarness.PlaceholderEntity);
    }

    [Fact]
    public void No_entity_is_missing_and_not_an_empty_string_to_send()
    {
        var settings = SabHarness.UnscopedSettings();

        settings.Entity.Should().BeNull("an installation that configures none has none");

        SabEntityScope.ResolveFor(settings).Should().Be(SabEntityResolution.Missing);

        // Null and never "": a transport handed an empty entity would send a request that names an
        // institution of zero characters, which is the "whatever Open SAB defaults to" case with
        // an extra step.
        SabEntityScope.EntityOf(settings).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void A_blank_entity_is_an_absent_one(string blank)
    {
        // An entity arrives through INT-03's settings form, and a cleared field posts a blank
        // rather than a null. Reading that as a value is how a call goes out scoped to nothing on
        // a network where "nothing" resolves to somebody.
        SabEntityScope.ResolveFor(SabHarness.UnscopedSettings(blank))
            .Should().Be(SabEntityResolution.Missing);

        SabEntityScope.EntityOf(SabHarness.UnscopedSettings(blank)).Should().BeNull();
    }

    [Fact]
    public void Absent_settings_are_missing_rather_than_an_exception()
    {
        // The caller may be the capability matrix of a tenant that has configured nothing at all.
        // "There is no entity" is the right answer to that; an exception would make a screen that
        // merely asks what is supported fail.
        SabEntityScope.ResolveFor(null).Should().Be(SabEntityResolution.Missing);
        SabEntityScope.IsScoped(null).Should().BeFalse();
        SabEntityScope.EntityOf(null).Should().BeNull();
    }

    [Fact]
    public void The_entity_a_call_would_carry_is_trimmed_and_otherwise_untouched()
    {
        var settings = SabHarness.UnscopedSettings($"  {SabHarness.PlaceholderEntity}  ");

        // Trimmed, because a form posts what was pasted. NOT case-folded and NOT padded: how Open
        // SAB compares the value is in the catalogue, and a normalisation that turned one
        // institution's code into another's is the whole risk this file guards.
        SabEntityScope.EntityOf(settings).Should().Be(SabHarness.PlaceholderEntity);
    }

    [Fact]
    public void Nothing_but_presence_is_judged()
    {
        // Deliberate, and the reason is symmetric with refusing to guess a field name: every shape
        // rule — a length, a character set, a prefix — is a property of the catalogue, and one
        // invented here would refuse a legitimate entity for a reason no document supports. The
        // test exists so that adding such a rule is a deliberate act with a failing test attached,
        // not a tidy-up.
        var odd = SabHarness.UnscopedSettings("x");
        var long_ = SabHarness.UnscopedSettings(new string('E', 300));
        var punctuated = SabHarness.UnscopedSettings("entité/01");

        foreach (var settings in new[] { odd, long_, punctuated })
            SabEntityScope.ResolveFor(settings).Should().Be(SabEntityResolution.Resolved);
    }

    [Fact]
    public void The_refusal_message_sends_the_reader_to_the_settings_screen_and_not_to_SBS()
    {
        var detail = SabEntityScope.MissingDetail();

        // The distinction is the deliverable. An operator who has just read ten refusals about a
        // missing catalogue will read an eleventh as one more "waiting for SBS" and forward it to
        // procurement — when the fix is one field on a screen they already have open.
        detail.Should().Contain("settings.entity");
        detail.Should().Contain("CIF", "the reader has to understand why a blank is dangerous");
        detail.Should().NotContain(SabSpecification.MissingDocument);
        detail.Should().NotContain(SabSpecification.PlanReference);
    }

    [Fact]
    public void The_rule_does_not_depend_on_the_rest_of_the_connection()
    {
        // A fully coordinated connection — base URL, vault reference, rate limit, timeout — is
        // still unscoped without an entity. Conflating "well configured" with "safe to call" is
        // precisely how a call goes out against the default entity of a shared installation.
        var configured = new SabSettings
        {
            BaseUrl = "https://sab.example.invalid",
            CredentialVaultRef = "vault://placeholder",
            RateLimitPerMinute = 120,
            TimeoutSeconds = 30,
        };

        SabEntityScope.ResolveFor(configured).Should().Be(SabEntityResolution.Missing);
    }
}
