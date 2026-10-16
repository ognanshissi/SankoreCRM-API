namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Domain;
using Xunit;

/// <summary>
/// TEL-B-04's status rule: « un statut inconnu est journalisé en alerte et l'appel <b>garde son
/// dernier statut connu</b> ».
///
/// <para>
/// <b>The rule is the deliverable, not the table.</b> The vocabulary is open question 2 and
/// cannot be obtained from anything but a test account, so the table is empty — and these tests
/// pin that it is empty ON PURPOSE, pin that nothing is guessed, and pin the behaviour the rule
/// describes so the day a token is added the rule is already right.
/// </para>
/// </summary>
public sealed class RingoverStatusTranslationTests
{
    [Fact]
    public void The_table_is_empty_until_a_test_account_is_obtained()
    {
        // This assertion is written to FAIL the day somebody adds a token — which is correct: it
        // is the moment to read this file, the moment the account arrived, and the moment the
        // mapping must be justified from observed traffic rather than from a public page. Deleting
        // this test is part of that change, not a way around it.
        RingoverStatusTranslation.Known.Should().BeEmpty(
            "the provider's status vocabulary is open question 2 and nothing is guessed");
    }

    [Theory]
    [InlineData("ANSWERED")]
    [InlineData("answered")]
    [InlineData("completed")]
    [InlineData("missed")]
    [InlineData("no_answer")]
    [InlineData("VOICEMAIL")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public void A_plausible_token_is_NOT_translated(string token)
    {
        // Every one of these looks like it ought to work, and that is exactly why none of them
        // does. A guessed status SUCCEEDS: the journal takes it, the call row changes, and nothing
        // ever reports it — so a conversation is recorded as missed and somebody is rung to
        // apologise for a call that already happened, or a missed call stops a lead's
        // first-contact clock for a conversation nobody had.
        var verdict = RingoverStatusTranslation.Translate(token);

        verdict.Status.Should().BeNull();
        verdict.KeepsLastKnownStatus.Should().BeTrue();
        verdict.DeservesAlert.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_token_is_reported_normalised_so_somebody_can_add_it()
    {
        var verdict = RingoverStatusTranslation.Translate("  SOME_TOKEN  ");

        // The token is the one piece of the provider's vocabulary worth putting in a log line: it
        // is what somebody copies into the table.
        verdict.Token.Should().Be("SOME_TOKEN");
        verdict.Missing.Should().BeFalse();
        verdict.DeservesAlert.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_ABSENT_status_keeps_the_call_where_it_is_and_does_NOT_alert(string? token)
    {
        var verdict = RingoverStatusTranslation.Translate(token);

        verdict.KeepsLastKnownStatus.Should().BeTrue();
        verdict.Missing.Should().BeTrue();

        // Told apart from an unknown token because they are different faults, and only one of them
        // is worth waking somebody for: a payload carrying no status is ordinary for event types
        // that are not about a status change at all, and alerting on it would alert on every one.
        verdict.DeservesAlert.Should().BeFalse();
        verdict.Token.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_ever_degrades_to_a_plausible_DEFAULT()
    {
        // The failure this guards is the tempting one: "if we cannot read it, call it Failed".
        // TelCall's four terminal statuses accept nothing further, so that guess would be
        // PERMANENT — a conversation frozen as a failure, and no later reading able to correct it.
        foreach (var token in new[] { "anything", "???", "0", "null", "unknown" })
        {
            RingoverStatusTranslation.Translate(token).Status.Should().BeNull();
        }
    }

    [Fact]
    public void Every_entry_in_the_table_whenever_one_is_added_must_name_a_real_status()
    {
        // Vacuous today and deliberately kept: it is the guard that catches a typo on the day the
        // table is filled in, when a mapping to a cast integer outside the enum would compile and
        // write a status nothing in this module can read.
        foreach (var (token, status) in RingoverStatusTranslation.Known)
        {
            Enum.IsDefined(status).Should().BeTrue($"'{token}' maps to an undeclared status");
            token.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_translation_is_case_insensitive_so_capitalisation_is_not_part_of_the_contract()
    {
        // Asserted on the comparer rather than on a lookup, because the table is empty: a
        // dictionary built with an ordinal comparer would make one token two the day the document
        // spells it differently in two places, which documents do.
        RingoverStatusTranslation.Known.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }
}
