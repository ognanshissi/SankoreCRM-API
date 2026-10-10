namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Features.CustomerLink;
using Xunit;

/// <summary>
/// Pins the auto-attachment rule of BNF-B-22: "correspondance unique et exacte : rattachement
/// automatique ; correspondance partielle ou multiple : suggestion à confirmer par l'agent, jamais
/// de rattachement silencieux".
/// </summary>
public sealed class CustomerMatchPolicyTests
{
    private static CustomerMatch Match(params string[] matchedOn) => new(
        ClientId:      Guid.NewGuid(),
        ClientNumber:  "CLI-0001",
        DisplayName:   "Aya Kouassi",
        AgencyId:      Guid.NewGuid(),
        AdvisorUserId: null,
        Status:        "Active",
        KycStatus:     "Approved",
        MatchedOn:     matchedOn);

    [Theory]
    [InlineData("identityDocument")]
    [InlineData("email")]
    public void A_single_person_identifying_match_is_attached_automatically(string signal)
    {
        CustomerMatchPolicy.IsAutoAttachable(Match(signal), totalMatches: 1).Should().BeTrue();
    }

    [Fact]
    public void A_single_match_on_phone_alone_is_only_a_suggestion()
    {
        // Two people sharing a handset is an ordinary situation in this market — a family phone, a
        // shop line. A phone identifies a CONNECTION, not a person, so it never attaches alone.
        CustomerMatchPolicy.IsAutoAttachable(Match("phone"), totalMatches: 1).Should().BeFalse();
    }

    [Fact]
    public void A_single_match_on_phone_and_document_is_attached()
    {
        CustomerMatchPolicy
            .IsAutoAttachable(Match("phone", "identityDocument"), totalMatches: 1)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Several_candidates_are_never_attached_however_strong_each_one_looks(int total)
    {
        // Picking between two clients who both match on an identity document is not a decision
        // code should make — it means the data is wrong somewhere, and an agent has to look.
        CustomerMatchPolicy
            .IsAutoAttachable(Match("identityDocument", "email", "phone"), total)
            .Should().BeFalse();
    }

    [Fact]
    public void No_candidate_attaches_nothing()
    {
        CustomerMatchPolicy.IsAutoAttachable(Match("email"), totalMatches: 0).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_signal_does_not_attach()
    {
        // Defensive: if M01 ever reports a new match reason, the default must be "ask a human",
        // not "attach on something this module has never heard of".
        CustomerMatchPolicy
            .IsAutoAttachable(Match("phoneticName"), totalMatches: 1)
            .Should().BeFalse();
    }
}
