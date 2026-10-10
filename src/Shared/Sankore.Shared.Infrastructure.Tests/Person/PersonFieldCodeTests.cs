namespace Sankore.Shared.Infrastructure.Tests.Person;

using FluentAssertions;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The closed list of required-field codes (BNF-B-04/B-06). It exists because the codes used to be
/// free text, which made a typo in a product's configuration a requirement that silently never
/// applied — the worst failure for a list whose only purpose is to gate a conversion.
/// </summary>
public sealed class PersonFieldCodeTests
{
    [Theory]
    [InlineData("LAST_NAME")]
    [InlineData("last_name")]
    [InlineData("  Birth_Date  ")]
    public void A_known_code_is_recognised_however_it_was_typed(string code)
        => PersonFieldCode.IsKnown(code).Should().BeTrue();

    [Theory]
    [InlineData("favouriteColour")]
    [InlineData("dateOfBirth")]   // the plausible-looking spelling that is NOT the code
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not(string? code)
        => PersonFieldCode.IsKnown(code).Should().BeFalse();

    [Fact]
    public void Normalize_gives_one_spelling_per_field()
    {
        PersonFieldCode.Normalize(" birth_date ").Should().Be(PersonFieldCode.BirthDate);
        PersonFieldCode.Normalize("NoSuchField").Should().BeNull();
    }

    [Fact]
    public void A_household_member_has_no_telephone_number_of_their_own()
    {
        // The scope is what stops the completeness check reporting a gap nobody can close: there
        // is no column for a spouse's phone, e-mail, national id or desired amount.
        PersonFieldCode.ScopeOf(PersonFieldCode.Phone)
            .Should().Be(PersonFieldScope.PrincipalOnly);
        PersonFieldCode.ScopeOf(PersonFieldCode.Email)
            .Should().Be(PersonFieldScope.PrincipalOnly);
        PersonFieldCode.ScopeOf(PersonFieldCode.NationalId)
            .Should().Be(PersonFieldScope.PrincipalOnly);
        PersonFieldCode.ScopeOf(PersonFieldCode.DesiredAmount)
            .Should().Be(PersonFieldScope.PrincipalOnly);

        PersonFieldCode.ScopeOf(PersonFieldCode.BirthDate)
            .Should().Be(PersonFieldScope.EveryPerson);
        PersonFieldCode.ScopeOf(PersonFieldCode.PepStatus)
            .Should().Be(PersonFieldScope.EveryPerson);
    }

    [Fact]
    public void Every_declared_constant_appears_in_All()
    {
        // Guards the one way this type can rot: a constant added without its entry in All is a
        // code an administrator cannot configure, and the omission is invisible.
        var constants = typeof(PersonFieldCode)
            .GetFields(System.Reflection.BindingFlags.Public
                     | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.Should().NotBeEmpty("otherwise this test asserts nothing");
        constants.Should().OnlyContain(c => PersonFieldCode.All.ContainsKey(c));
        PersonFieldCode.All.Should().HaveCount(constants.Count);
    }
}
