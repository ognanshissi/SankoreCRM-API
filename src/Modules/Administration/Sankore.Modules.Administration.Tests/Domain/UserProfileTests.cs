namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class UserProfileTests
{
    private static UserProfile Fresh() => UserProfile.Create(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Self_update_stores_contacts_preferences_and_blanks_as_null()
    {
        var p = Fresh();

        p.UpdateBySelf(" +225 07 00 00 00 00 ", "  ", "Ibrahim Sanogo", "EN", "Africa/Lagos", AmountFormat.DotGrouped);

        p.WorkNumber.Contact.Should().Be("+225 07 00 00 00 00");
        p.WhatsAppNumber.Should().BeNull();
        p.Signature.Should().Be("Ibrahim Sanogo");
        p.DefaultLanguage.Should().Be("en");
        p.TimeZone.Should().Be("Africa/Lagos");
        p.AmountFormat.Should().Be(AmountFormat.DotGrouped);
        p.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public void An_unknown_time_zone_is_refused()
    {
        var act = () => Fresh().UpdateBySelf(null, null, null, "fr", "Mars/Olympus", AmountFormat.SpaceGrouped);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_unreadable_time_zone_resolves_to_utc()
        => UserProfile.ResolveTimeZone("garbage").Should().Be(TimeZoneInfo.Utc);
}

public sealed class UserAvatarTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];

    [Fact]
    public void The_type_is_read_from_the_bytes()
    {
        UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), Png).ContentType.Should().Be("image/png");
        UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), Jpeg).ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public void Anything_but_jpg_or_png_is_refused()
    {
        var gif = "GIF89a"u8.ToArray();
        var act = () => UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), gif);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void More_than_two_megabytes_is_refused()
    {
        var big = new byte[UserAvatar.MaxSizeBytes + 1];
        Jpeg.CopyTo(big, 0);
        var act = () => UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), big);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Renders_as_a_data_url()
        => UserAvatar.Create(Guid.NewGuid(), Guid.NewGuid(), Png).ToDataUrl().Should().StartWith("data:image/png;base64,");
}
