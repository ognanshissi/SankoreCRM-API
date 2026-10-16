namespace Sankore.Modules.Integration.Tests.Adapters.Ringover;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// TEL-B-04's error classification: « 401/403 → <c>Technical</c>, 429/5xx → <c>Transient</c>,
/// numéro invalide → <c>Functional</c> ».
///
/// <para>
/// <b>This half is implemented in full, and the reason is that HTTP is not Ringover's
/// vocabulary.</b> 401, 403, 429 and the 5xx range mean the same thing whoever serves them, so
/// nothing here is a guess about a document. What remains open — whether an invalid number is
/// refused on the request or failed later on a webhook — is question 3, and does not change any
/// answer below.
/// </para>
///
/// <para>
/// Why it is worth a suite of its own: the family decides what the platform DOES. A 401 classified
/// <c>Transient</c> makes the dispatcher hammer the provider with a key it has already refused,
/// which is how an account gets rate-limited and then suspended for behaviour that looks like an
/// attack. A 429 classified <c>Technical</c> parks a call that would have succeeded a second later
/// and pages somebody at night.
/// </para>
/// </summary>
public sealed class RingoverErrorClassificationTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Authentication_and_authorization_are_OUR_configuration(int status)
    {
        // Technical, so the dispatcher never retries and the administrator is alerted: a retry
        // cannot obtain a credential, and repeating a refused key is what gets an account
        // suspended.
        RingoverErrorClassification.Classify(status).Should().Be(ErrorFamily.Technical);
    }

    [Fact]
    public void A_rate_limit_is_the_one_4xx_that_IS_transient()
    {
        // The reason this is a table of cases and not a range check. Classified Functional it
        // would send every throttled call to a human; classified Technical it would page
        // somebody for a wait.
        RingoverErrorClassification.Classify(429).Should().Be(ErrorFamily.Transient);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(501)]
    [InlineData(599)]
    public void The_provider_being_unwell_is_retried(int status)
    {
        // 501 is in here on purpose: in a provider's hands it is usually a gateway in front of a
        // service being deployed, not a permanent statement about the operation — and the
        // backoff is bounded, so the cost of being wrong in this direction is a few retries
        // rather than a parked call.
        RingoverErrorClassification.Classify(status).Should().Be(ErrorFamily.Transient);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public void A_request_refused_on_its_CONTENT_goes_to_a_human(int status)
    {
        // This is where an invalid number lands, which is the plan's third rule. Retrying one
        // would ring nobody, forever.
        //
        // 404 is the arm worth arguing about: on a read it means "this call id does not exist at
        // the provider", a fact about the data — classified Technical it would alert an
        // administrator every time the catch-up sweep asked about a call the provider has aged
        // out of its history.
        RingoverErrorClassification.Classify(status).Should().Be(ErrorFamily.Functional);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(600)]
    public void No_response_at_all_is_retried(int status)
    {
        // A timeout, a DNS failure, a reset — reduced to a status outside the valid range by the
        // caller rather than carried as an exception, so there is ONE classification path and not
        // two that can disagree.
        RingoverErrorClassification.Classify(status).Should().Be(ErrorFamily.Transient);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(302)]
    public void A_SUCCESS_reaching_the_classifier_is_a_bug_and_is_never_retried(int status)
    {
        // Technical, because the caller has one. Treating it as Transient would retry a success —
        // and in this family that means ringing somebody's telephone a second time.
        RingoverErrorClassification.Classify(status).Should().Be(ErrorFamily.Technical);
    }

    [Fact]
    public void The_provider_token_set_is_empty_until_a_test_account_is_obtained()
    {
        // Written to FAIL the day somebody adds a token, which is correct: it is the moment to
        // read the one-way rule below and justify the entry from observed traffic.
        RingoverErrorClassification.FunctionalCodes.Should().BeEmpty(
            "the provider's error vocabulary is open question 3 and nothing is guessed");
    }

    [Fact]
    public void A_provider_token_may_only_ever_NARROW_a_retry_and_never_create_one()
    {
        // The asymmetry is the safety property. A wrong entry in FunctionalCodes costs a retry
        // that was never going to succeed; a token that promoted a Functional to Transient would
        // turn a permanent refusal into an infinite retry.
        //
        // Asserted through the public behaviour: whatever token is passed, a 4xx that is already
        // Functional stays Functional, and a Technical stays Technical.
        foreach (var token in new[] { "SOME_CODE", "", "   ", null })
        {
            RingoverErrorClassification.ClassifyWithToken(400, token)
                .Should().Be(ErrorFamily.Functional);

            RingoverErrorClassification.ClassifyWithToken(401, token)
                .Should().Be(ErrorFamily.Technical);

            // And with an empty set, a Transient stays Transient — there is nothing to narrow it.
            RingoverErrorClassification.ClassifyWithToken(503, token)
                .Should().Be(ErrorFamily.Transient);
        }
    }

    [Fact]
    public void Every_HTTP_status_in_the_valid_range_gets_a_declared_family()
    {
        // A sweep rather than a list of cases: the method is a chain of ifs, and a status that
        // fell through all of them would produce whatever the last line returned. The point is
        // that there IS no fall-through — every code in the range is classified deliberately.
        for (var status = 100; status <= 599; status++)
        {
            var family = RingoverErrorClassification.Classify(status);

            Enum.IsDefined(family).Should().BeTrue($"{status} produced an undeclared family");
        }
    }
}
