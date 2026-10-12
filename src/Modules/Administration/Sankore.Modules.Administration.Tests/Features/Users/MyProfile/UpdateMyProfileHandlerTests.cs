namespace Sankore.Modules.Administration.Tests.Features.Users.MyProfile;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.MyProfile;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class UpdateMyProfileHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public UpdateMyProfileHandlerTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedUserAsync(bool withProfile)
    {
        await using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Ibrahim", "Sanogo", "i.sanogo@imf.ci");
        db.Users.Add(user);
        if (withProfile) db.UserProfiles.Add(UserProfile.Create(_tenantId, user.Id));
        await db.SaveChangesAsync();
        return user.Id;
    }

    private UpdateMyProfileHandler Handler(Guid userId)
    {
        var current = Substitute.For<ICurrentUser>();
        current.Id.Returns(userId);
        return new UpdateMyProfileHandler(_factory.CreateContext(), current);
    }

    private static UpdateMyProfileRequest Request(string timeZone = "Africa/Abidjan") => new(
        "Ibrahima", "Sanogo", "+225 07 00 00 00 00", null, "Ibrahima Sanogo\nAgence Plateau",
        "en", timeZone, "CommaGrouped");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saves_the_profile_creating_it_when_missing(bool withProfile)
    {
        var userId = await SeedUserAsync(withProfile);

        var result = await Handler(userId).Handle(new UpdateMyProfileCommand(Request()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var db = _factory.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.FullName.Should().Be("Ibrahima Sanogo");
        user.PreferredLanguage.Should().Be("en");
        var profile = await db.UserProfiles.SingleAsync(p => p.UserId == userId);
        profile.Signature.Should().Be("Ibrahima Sanogo\nAgence Plateau");
        profile.AmountFormat.Should().Be(AmountFormat.CommaGrouped);
        profile.DefaultLanguage.Should().Be("en");
    }

    [Fact]
    public async Task An_unknown_time_zone_fails_without_saving()
    {
        var userId = await SeedUserAsync(withProfile: true);

        var result = await Handler(userId).Handle(
            new UpdateMyProfileCommand(Request("Nowhere/Land")), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await using var db = _factory.CreateContext();
        (await db.Users.SingleAsync(u => u.Id == userId)).FullName.Should().Be("Ibrahim Sanogo");
    }
}
