namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain.Matching;
using Xunit;

public class ClientMatchScorerTests
{
    private static readonly Guid AgencyA = Guid.NewGuid();
    private static readonly Guid AgencyB = Guid.NewGuid();

    [Fact]
    public void A_pair_matching_every_signal_should_score_the_documented_weights_capped_at_100()
    {
        var left = Reference();
        var right = Reference() with { ClientId = Guid.NewGuid() };

        var score = ClientMatchScorer.Score(left, right);

        var matchedWeight = score.Signals.Where(s => s.Matched).Sum(s => s.Weight);
        matchedWeight.Should().Be(
            ClientMatchScorer.WeightIdentityDocument
            + ClientMatchScorer.WeightPhoneticBoth
            + ClientMatchScorer.WeightDateOfBirth
            + ClientMatchScorer.WeightSameAgency
            + ClientMatchScorer.WeightFatherName
            + ClientMatchScorer.WeightMotherName);
        matchedWeight.Should().Be(130);
        score.Score.Should().Be(ClientMatchScorer.MaxScore);
    }

    [Fact]
    public void Two_clients_sharing_an_identity_document_should_score_at_least_60()
    {
        var left = Reference();
        var right = new ClientMatchInput(
            Guid.NewGuid(), null, null, null, AgencyB, null, null, "bi-doc-1");

        var score = ClientMatchScorer.Score(left, right);

        score.Score.Should().BeGreaterThanOrEqualTo(ClientMatchScorer.WeightIdentityDocument);
        score.Score.Should().Be(60);
        score.Signals.Single(s => s.Key == ClientMatchScorer.SignalIdentityDocument).Matched.Should().BeTrue();
    }

    [Fact]
    public void One_phonetic_key_in_common_should_weigh_less_than_two()
    {
        var left = Reference();
        var onlyPrimary = new ClientMatchInput(
            Guid.NewGuid(), "WTR", "SMTHNGLS", null, AgencyB, null, null, null);

        var score = ClientMatchScorer.Score(left, onlyPrimary);

        score.Score.Should().Be(ClientMatchScorer.WeightPhoneticOne);
        score.Signals.Single(s => s.Key == ClientMatchScorer.SignalPhoneticOne).Matched.Should().BeTrue();
        score.Signals.Single(s => s.Key == ClientMatchScorer.SignalPhoneticBoth).Matched.Should().BeFalse();
    }

    [Fact]
    public void Swapped_first_and_last_name_should_still_count_as_one_phonetic_match()
    {
        var left = new ClientMatchInput(Guid.NewGuid(), "WTR", "AM", null, AgencyA, null, null, null);
        var right = new ClientMatchInput(Guid.NewGuid(), "AM", "WTR", null, AgencyB, null, null, null);

        ClientMatchScorer.Score(left, right).Score.Should().Be(ClientMatchScorer.WeightPhoneticOne);
    }

    [Fact]
    public void Two_unrelated_clients_of_the_same_agency_should_only_earn_the_agency_point()
    {
        var left = new ClientMatchInput(Guid.NewGuid(), "WTR", "AM", "bi-dob-1", AgencyA, "Ibrahim", "Fatou", "bi-doc-1");
        var right = new ClientMatchInput(Guid.NewGuid(), "KN", "SK", "bi-dob-2", AgencyA, "Yaya", "Adjoua", "bi-doc-2");

        ClientMatchScorer.Score(left, right).Score.Should().Be(ClientMatchScorer.WeightSameAgency);
    }

    [Fact]
    public void Missing_values_should_never_count_as_a_match()
    {
        var blank = new ClientMatchInput(Guid.NewGuid(), null, "", null, Guid.Empty, "  ", null, null);
        var other = new ClientMatchInput(Guid.NewGuid(), null, "", null, Guid.Empty, "  ", null, null);

        var score = ClientMatchScorer.Score(blank, other);

        score.Score.Should().Be(0);
        score.Signals.Should().OnlyContain(s => !s.Matched);
    }

    [Fact]
    public void Parent_names_should_be_compared_after_normalization()
    {
        var left = new ClientMatchInput(Guid.NewGuid(), null, null, null, Guid.Empty, "Ouattara", "Traoré", null);
        var right = new ClientMatchInput(Guid.NewGuid(), null, null, null, Guid.Empty, "wattara", "Traore", null);

        ClientMatchScorer.Score(left, right).Score
            .Should().Be(ClientMatchScorer.WeightFatherName + ClientMatchScorer.WeightMotherName);
    }

    // ── Fingerprint ─────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_should_be_a_stable_sha256_hex_of_the_comparable_fields()
    {
        var input = Reference();

        var first = ClientMatchScorer.Fingerprint(input);
        var second = ClientMatchScorer.Fingerprint(input);

        first.Should().Be(second);
        first.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Fingerprint_should_ignore_the_client_id()
    {
        var input = Reference();

        ClientMatchScorer.Fingerprint(input)
            .Should().Be(ClientMatchScorer.Fingerprint(input with { ClientId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("phonetic")]
    [InlineData("dob")]
    [InlineData("agency")]
    [InlineData("father")]
    [InlineData("document")]
    public void Fingerprint_should_change_when_a_comparable_field_changes(string changedField)
    {
        var input = Reference();
        var mutated = changedField switch
        {
            "phonetic" => input with { PhoneticKeyPrimary = "KN" },
            "dob" => input with { DateOfBirthBlindIndex = "bi-dob-2" },
            "agency" => input with { AgencyId = AgencyB },
            "father" => input with { FatherName = "Yaya" },
            _ => input with { IdentityDocumentNumberBlindIndex = "bi-doc-2" },
        };

        ClientMatchScorer.Fingerprint(mutated).Should().NotBe(ClientMatchScorer.Fingerprint(input));
    }

    private static ClientMatchInput Reference() => new(
        ClientId: Guid.NewGuid(),
        PhoneticKeyPrimary: "WTR",
        PhoneticKeySecondary: "AM",
        DateOfBirthBlindIndex: "bi-dob-1",
        AgencyId: AgencyA,
        FatherName: "Ibrahim",
        MotherName: "Fatoumata",
        IdentityDocumentNumberBlindIndex: "bi-doc-1");
}
