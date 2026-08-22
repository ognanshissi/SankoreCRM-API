namespace Sankore.Modules.Administration.Tests.TestSupport;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Builds NSubstitute mocks for the ASP.NET Core Identity managers.
/// The managers expose their methods as virtual, so NSubstitute can
/// intercept and configure them without needing a real store or database.
/// </summary>
internal static class IdentityMockFactory
{
    public static UserManager<AppUser> BuildUserManager()
    {
        var store = Substitute.For<IUserStore<AppUser>>();
        var manager = Substitute.For<UserManager<AppUser>>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            NullLogger<UserManager<AppUser>>.Instance
        );
        return manager;
    }

    public static RoleManager<AppRole> BuildRoleManager()
    {
        var store = Substitute.For<IRoleStore<AppRole>>();
        var manager = Substitute.For<RoleManager<AppRole>>(
            store,
            Array.Empty<IRoleValidator<AppRole>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<AppRole>>.Instance
        );
        return manager;
    }
}
