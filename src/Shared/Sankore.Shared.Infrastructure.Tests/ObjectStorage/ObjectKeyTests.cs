namespace Sankore.Shared.Infrastructure.Tests.ObjectStorage;

using FluentAssertions;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// The key rule is the one place traversal is decided for every backend at once, so it is pinned
/// on its own rather than only through a backend that happens to resolve paths.
/// </summary>
public sealed class ObjectKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("imports/2026/leads.xlsx")]
    [InlineData("3f2a1b/ab/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.kycobj")]
    [InlineData("a.b.c")]
    [InlineData("dossier-été_2026/fichier (1).pdf")]
    public void A_well_formed_key_should_be_accepted(string key)
        => ObjectKey.IsValid(key).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/absolute")]            // would mean the filesystem root
    [InlineData("trailing/")]            // names a folder, not an object
    [InlineData("a//b")]                 // empty segment
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("a/./b")]
    [InlineData("a/..")]
    [InlineData(".")]
    [InlineData("..\\windows\\system32")] // backslash: a separator on Windows, a character on S3
    [InlineData("stream:$DATA")]          // NTFS alternate data stream
    [InlineData("a\nb")]                  // control character
    [InlineData("a\0b")]
    [InlineData("wild*card")]
    public void A_key_that_is_not_portable_or_not_contained_should_be_refused(string? key)
        => ObjectKey.IsValid(key).Should().BeFalse();

    [Fact]
    public void A_key_longer_than_the_s3_ceiling_should_be_refused()
    {
        ObjectKey.IsValid(new string('a', ObjectKey.MaxLength)).Should().BeTrue();
        ObjectKey.IsValid(new string('a', ObjectKey.MaxLength + 1)).Should().BeFalse();
    }

    [Fact]
    public void Validate_should_name_the_offending_key_and_the_parameter()
    {
        var act = () => ObjectKey.Validate("../escape", "objectKey");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("../escape");
    }
}
