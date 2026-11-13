namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class CompanyLogoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
    private static readonly byte[] WebP = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x00];

    [Theory]
    [MemberData(nameof(Images))]
    public void The_type_is_read_from_the_bytes(byte[] content, string expected)
    {
        CompanyLogo.ContentTypeOf(content).Should().Be(expected);
    }

    public static TheoryData<byte[], string> Images() => new()
    {
        { Png, "image/png" },
        { Jpeg, "image/jpeg" },
        { WebP, "image/webp" },
    };

    [Fact]
    public void An_svg_is_refused_even_though_it_is_an_image()
    {
        // Un SVG est un document qui peut porter du script, et la route publique le servirait
        // depuis l'origine de l'API.
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

        var act = () => CompanyLogo.ContentTypeOf(svg);

        act.Should().Throw<DomainException>().WithMessage("*PNG, JPG ou WebP*");
    }

    [Fact]
    public void A_file_over_one_megabyte_is_refused()
    {
        var big = new byte[CompanyLogo.MaxSizeBytes + 1];
        Png.CopyTo(big, 0);

        var act = () => CompanyLogo.ContentTypeOf(big);

        act.Should().Throw<DomainException>().WithMessage("*1 Mo*");
    }

    [Fact]
    public void Replacing_moves_the_date_so_the_public_url_changes()
    {
        var logo = CompanyLogo.Create(Guid.NewGuid(), "l1.first", "image/png");
        var first = logo.UpdatedAt;

        Thread.Sleep(5);
        logo.Replace("l1.second", "image/jpeg");

        logo.ContentType.Should().Be("image/jpeg");
        logo.StorageRef.Should().Be("l1.second");
        logo.UpdatedAt.Should().BeAfter(first);
    }

    [Fact]
    public void Moving_to_object_storage_keeps_the_date_so_the_published_url_still_works()
    {
        var logo = CompanyLogo.Create(Guid.NewGuid(), "l1.first", "image/png");
        var date = logo.UpdatedAt;

        logo.MoveToStorage("l1.moved");

        logo.StorageRef.Should().Be("l1.moved");
        logo.Content.Should().BeNull();
        logo.UpdatedAt.Should().Be(date);
    }
}
