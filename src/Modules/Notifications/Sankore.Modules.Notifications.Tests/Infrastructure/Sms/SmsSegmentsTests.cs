namespace Sankore.Modules.Notifications.Tests.Infrastructure.Sms;

using FluentAssertions;
using Sankore.Modules.Notifications.Infrastructure.Sms;
using Xunit;

/// <summary>
/// How many segments a message costs.
///
/// <para>
/// <b>This is the one piece of arithmetic in the SMS channel that shows up on an invoice rather
/// than in a log.</b> A GSM-7 message fits 160 characters; a single character outside that
/// alphabet — one "é" — switches the whole message to UCS-2 and drops the budget to 70, which
/// turns a one-segment message into three. Counting <c>Length / 160</c> would report one, the
/// provider would bill three, and nobody would find out until a month later.
/// </para>
/// </summary>
public sealed class SmsSegmentsTests
{
    [Fact]
    public void An_empty_body_costs_nothing()
    {
        SmsSegments.Count(null).Should().Be(0);
        SmsSegments.Count(string.Empty).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(159)]
    [InlineData(160)]
    public void Up_to_160_GSM7_characters_is_one_segment(int length)
    {
        SmsSegments.Count(new string('a', length)).Should().Be(1);
    }

    [Fact]
    public void The_161st_GSM7_character_costs_a_second_segment()
    {
        // And the second segment is 153, not 160: seven characters of each go to the header that
        // tells the handset how to reassemble them. A test at 161 is what pins that the boundary
        // is where it is.
        SmsSegments.Count(new string('a', 161)).Should().Be(2);
        SmsSegments.Count(new string('a', 306)).Should().Be(2);
        SmsSegments.Count(new string('a', 307)).Should().Be(3);
    }

    [Fact]
    public void One_accented_character_more_than_halves_the_budget()
    {
        // THE case this class exists for. A hundred characters is comfortably one segment in
        // GSM-7; the same hundred with one "œ" in them is UCS-2, where the budget is 70, so it
        // costs two. The seeded French bodies are written without accents for exactly this, which
        // looks like a typo in SmsSeeder and is not.
        var plain = new string('a', 100);

        SmsSegments.Count(plain).Should().Be(1);
        SmsSegments.Count(plain + "œ").Should().Be(2);
    }

    [Fact]
    public void The_accented_characters_GSM7_DOES_have_stay_in_one_segment()
    {
        // Not every accent is outside the alphabet: è, é, ù, ì, ò, à, ä, ö, ñ, ü and Ç are all in
        // GSM-7. Without this case the rule above would read as "avoid accents", and a tenant
        // would be told to write worse French than they need to.
        SmsSegments.Count("Votre code expire dans dix minutes, à bientôt").Should().Be(1);
        SmsSegments.Count("éèùìòÇäöñüà").Should().Be(1);
    }

    [Theory]
    [InlineData("^")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("~")]
    [InlineData("|")]
    [InlineData("€")]
    [InlineData("\\")]
    public void The_GSM7_extension_characters_cost_TWO_septets_each(string extended)
    {
        // They are reachable in GSM-7 but only through an escape, so each one costs two of the
        // 160. A body of 80 of them is exactly full; 81 overflows. Counting them as one would
        // under-report a message carrying a euro sign or a brace — which a template with
        // {{ placeholders }} does not, because Scriban consumes those, but one quoting a currency
        // does.
        SmsSegments.Count(string.Concat(Enumerable.Repeat(extended, 80))).Should().Be(1);
        SmsSegments.Count(string.Concat(Enumerable.Repeat(extended, 81))).Should().Be(2);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(70, 1)]
    [InlineData(71, 2)]
    [InlineData(134, 2)]
    [InlineData(135, 3)]
    public void UCS2_is_counted_against_70_then_67(int length, int expected)
    {
        // "œ" and NOT "é": é is in GSM-7 (the case below pins that), so a body carrying one stays
        // on the 160-character budget and this table would assert nothing. Writing it with é is
        // the mistake this comment exists to stop being made again — it passes at 70 and fails at
        // 71 for the wrong reason, which reads as a bug in the counter.
        var body = "œ" + new string('a', length - 1);

        SmsSegments.Count(body).Should().Be(expected);
    }

    [Fact]
    public void An_emoji_outside_the_BMP_costs_TWO_units_and_not_one()
    {
        // UCS-2 bills UTF-16 code units, and an emoji above U+FFFF is a surrogate pair. Counting
        // runes would say 35 of these fit in a segment; the provider bills for 70 units, so 35
        // pairs is exactly full and 36 overflows.
        var pair = "😀";

        pair.Length.Should().Be(2, "the test depends on this being a surrogate pair");

        SmsSegments.Count(string.Concat(Enumerable.Repeat(pair, 35))).Should().Be(1);
        SmsSegments.Count(string.Concat(Enumerable.Repeat(pair, 36))).Should().Be(2);
    }

    [Fact]
    public void The_seeded_OTP_bodies_fit_one_segment()
    {
        // The platform's own messages, which every tenant pays for. A body that grew past the
        // boundary during an edit would double the cost of every one-time code in the product,
        // and the seeder logs a warning — this is what fails the build instead.
        const string fr =
            "Votre code de verification est {{ code }}. Il expire dans {{ minutes }} minutes. "
            + "Ne le communiquez a personne.";

        const string en =
            "Your verification code is {{ code }}. It expires in {{ minutes }} minutes. "
            + "Do not share it with anyone.";

        SmsSegments.Count(fr).Should().Be(1);
        SmsSegments.Count(en).Should().Be(1);

        // And still one once rendered, which is what is actually sent: the placeholders shrink.
        SmsSegments.Count(fr.Replace("{{ code }}", "123456").Replace("{{ minutes }}", "10"))
            .Should().Be(1);
    }
}
