namespace Sankore.Modules.Integration.Tests.Adapters.Orass;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// ASS-06, criterion 3 — the intermediary / apporteur code, and the branch that scopes it.
/// <b>Deliverable in full</b>: it is a pure function of the settings, and the decision about what
/// to do in its absence is ours.
///
/// <para>
/// What these tests defend is the DIRECTION of the failure. Refusing a submission that might have
/// worked costs an administrator one field in a form and a retry; letting an unattributed one
/// through costs a policy booked under the insurer's default apporteur — real cover, in nobody's
/// portfolio, that no screen of this institution can find. The second is not recoverable by
/// noticing afterwards.
/// </para>
/// </summary>
public sealed class OrassIntermediaryScopeTests
{
    [Fact]
    public void A_configured_code_resolves()
    {
        OrassIntermediaryScope.ResolveFor(OrassHarness.ApiSettings())
            .Should().Be(OrassIntermediaryResolution.Resolved);

        OrassIntermediaryScope.IsAttributed(OrassHarness.ApiSettings()).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_code_is_no_code(string? code)
    {
        var settings = OrassHarness.UnattributedSettings(code);

        // Whitespace counts as absent deliberately. A cleared field on INT-03's settings form posts
        // " ", and treating it as a value would submit business under a blank apporteur — which is
        // precisely the "attributed to whatever the insurer defaults to" case.
        OrassIntermediaryScope.ResolveFor(settings)
            .Should().Be(OrassIntermediaryResolution.Missing);

        OrassIntermediaryScope.CodeOf(settings).Should().BeNull();
    }

    [Fact]
    public void Unreadable_settings_are_unattributed_rather_than_an_exception()
    {
        // The caller may be the capability matrix of a tenant that has configured no ORASS
        // connection at all, and "there is no intermediary code" is the correct answer to that, not
        // a fault.
        OrassIntermediaryScope.ResolveFor(null).Should().Be(OrassIntermediaryResolution.Missing);
        OrassIntermediaryScope.IsAttributed(null).Should().BeFalse();
        OrassIntermediaryScope.CodeOf(null).Should().BeNull();
    }

    [Fact]
    public void The_code_a_submission_would_carry_is_trimmed_and_nothing_else()
    {
        var settings = new OrassSettings
        {
            IntermediaryCode = $"  {OrassHarness.PlaceholderIntermediaryCode}  ",
        };

        // Trimming only. Case-folding or padding would be a guess about how the insurer compares
        // the value, and a guess that turned one distributor's code into another's is the whole risk
        // of this file. If this test ever has to change, the specification has arrived and says so.
        OrassIntermediaryScope.CodeOf(settings)
            .Should().Be(OrassHarness.PlaceholderIntermediaryCode);
    }

    [Fact]
    public void No_shape_rule_is_invented_for_a_code_we_have_no_document_for()
    {
        // Pinned so that adding one is a deliberate act. Length, character set, case and prefix are
        // all properties of the insurer's own numbering; a rule invented here would refuse a
        // legitimate apporteur code for a reason no document supports, which is the same class of
        // mistake as a guessed field name pointing the other way.
        foreach (var exotic in new[] { "a", "0", "x-1/2", "ÉÀÎ", new string('z', 400) })
        {
            OrassIntermediaryScope
                .IsAttributed(new OrassSettings { IntermediaryCode = exotic })
                .Should().BeTrue($"'{exotic}' may be exactly what this insurer issues");
        }
    }

    // ── The message ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_refusal_names_the_field_the_consequence_and_that_nothing_is_awaited()
    {
        var detail = OrassIntermediaryScope.MissingDetail();

        // The field, so the administrator knows where to go.
        detail.Should().Contain("IntermediaryCode");

        // The consequence, so they know why it is worth going. This is the half that distinguishes
        // ORASS's problem from SAB's: not "another institution's data leaks", but "the policy is
        // real and nobody can attribute it to us".
        detail.Should().Contain("default");

        // And explicitly NOT the supplier. Without that clause an operator who has just read ten
        // refusals about a missing specification reads an eleventh and forwards it to procurement,
        // when the fix is one field on a screen they already have open.
        detail.Should().Contain("nothing is being waited on");
        detail.Should().NotContain(IntegrationErrors.AdapterSpecificationPending);
    }

    [Fact]
    public void The_refusal_never_names_a_plausible_code()
    {
        // The tripwire for the mistake this whole chantier exists to avoid: an invented identifier
        // in a message is how an invented identifier reaches a mapper.
        OrassIntermediaryScope.MissingDetail()
            .Should().NotContain(OrassHarness.PlaceholderIntermediaryCode);
    }

    // ── The branch ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrassBranch.Iard, "non-life")]
    [InlineData(OrassBranch.Vie, "life")]
    public void A_known_branch_is_named_in_plain_words(OrassBranch branch, string expected)
    {
        var designation = OrassIntermediaryScope.Designation(branch);

        // Named in words and not only by the enum, because the operator reading a rejection has to
        // forward it to one of two companies and "Iard" is not a company name they would recognise.
        designation.Should().Contain(branch.ToString());
        designation.Should().Contain(expected);
    }

    [Fact]
    public void An_unknown_branch_names_both_rather_than_picking_one()
    {
        var designation = OrassIntermediaryScope.Designation(null);

        // The failure mode this prevents: sending somebody to the life company about a non-life
        // submission. In the CIMA zone the two are separate undertakings, so a wrong guess is not a
        // wrong label, it is a conversation with the wrong legal entity.
        designation.Should().Contain(nameof(OrassBranch.Iard));
        designation.Should().Contain(nameof(OrassBranch.Vie));
    }

    [Fact]
    public void The_branch_is_carried_but_never_verified_and_the_code_says_so()
    {
        // There is nothing to assert about verification, because verification is impossible without
        // the specification — which is the point. What CAN be asserted is that presence is all the
        // guard claims: a code belonging to the other undertaking passes it, and must, because
        // telling the two apart needs a document nobody here has.
        var wrongBranchButWellFormed = new OrassSettings
        {
            IntermediaryCode = OrassHarness.PlaceholderIntermediaryCode,
            Branch = OrassBranch.Vie,
        };

        OrassIntermediaryScope.IsAttributed(wrongBranchButWellFormed).Should().BeTrue(
            "presence is all that is verifiable here; whether the code belongs to the branch it is "
            + "paired with is question 5 of OrassSpecification.OpenQuestions, and the obligation "
            + "for the day it is answered is on CheckHealthAsync");
    }
}
