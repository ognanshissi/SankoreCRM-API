namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain.Matching;
using Xunit;

/// <summary>
/// The phonetic key is the whole basis of duplicate detection: these orthographic pairs are
/// the same family name spelled two ways, and they MUST land on one key.
/// </summary>
public class PhoneticKeyTests
{
    private readonly IPhoneticKeyCalculator _sut = new WestAfricanPhoneticKeyCalculator();

    [Theory]
    [InlineData("Ouattara", "Wattara")]
    [InlineData("Diallo", "Jallo")]
    [InlineData("Kouassi", "Kwasi")]
    [InlineData("Traoré", "Traore")]
    [InlineData("Koné", "Kone")]
    [InlineData("Yaou", "Yao")]
    [InlineData("Bamba", "Banba")]
    [InlineData("Cheikh", "Chek")]
    public void Orthographic_variants_of_the_same_name_should_share_one_key(string left, string right)
    {
        var leftKey = _sut.Compute(left);
        var rightKey = _sut.Compute(right);

        leftKey.Should().NotBeNullOrWhiteSpace();
        leftKey.Should().Be(rightKey);
    }

    [Theory]
    [InlineData("Ndiaye", "N'Diaye")]
    [InlineData("Dieng", "Jeng")]
    [InlineData("Kouamé", "Kwame")]
    [InlineData("Ouedraogo", "Wedraogo")]
    [InlineData("Tchibozo", "Chibozo")]
    public void Further_west_african_transliterations_should_also_share_one_key(string left, string right) =>
        _sut.Compute(left).Should().Be(_sut.Compute(right));

    [Theory]
    [InlineData("Ouattara", "Kone")]
    [InlineData("Diallo", "Bamba")]
    [InlineData("Yao", "Traore")]
    [InlineData("Cheikh", "Kouassi")]
    public void Genuinely_different_names_should_not_collapse_to_the_same_key(string left, string right) =>
        _sut.Compute(left).Should().NotBe(_sut.Compute(right));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- //")]
    public void A_blank_name_should_produce_no_key(string? name) =>
        _sut.Compute(name).Should().BeNull();

    [Fact]
    public void Accents_should_never_change_the_key()
    {
        _sut.Compute("Traoré").Should().Be(_sut.Compute("TRAORE"));
        _sut.Compute("Koné").Should().Be(_sut.Compute("kone"));
        _sut.Compute("Sangaré").Should().Be(_sut.Compute("Sangare"));
    }

    [Fact]
    public void The_key_should_be_stable_across_calls_and_case_insensitive() =>
        _sut.Compute("ouattara").Should().Be(_sut.Compute("OUATTARA"));

    [Fact]
    public void Normalizer_should_fold_the_documented_orthographic_variants()
    {
        WestAfricanNameNormalizer.Normalize("Ouattara").Should().Be("WATARA");
        WestAfricanNameNormalizer.Normalize("Wattara").Should().Be("WATARA");
        WestAfricanNameNormalizer.Normalize("Diallo").Should().Be("JALO");
        WestAfricanNameNormalizer.Normalize("Kouassi").Should().Be("KWASI");
        WestAfricanNameNormalizer.Normalize("Cheikh").Should().Be("CHEIK");
        WestAfricanNameNormalizer.Normalize("Banba").Should().Be("BAMBA");
        WestAfricanNameNormalizer.Normalize("Yaou").Should().Be("YA");
    }

    [Fact]
    public void Normalizer_should_drop_punctuation_and_keep_word_content()
    {
        WestAfricanNameNormalizer.Normalize("N'Diaye").Should().Be("NJAYE");
        WestAfricanNameNormalizer.Normalize("  traoré-koné ").Should().Be("TRAOREKONE");
        WestAfricanNameNormalizer.Normalize("   ").Should().BeEmpty();
    }
}
