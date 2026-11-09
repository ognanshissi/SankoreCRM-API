using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.StartingData;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.DemoUsers;

/// <summary>
/// A fictitious account of a demo institution (<c>docs/donnees-demarrage-demo.md</c>, owner's choice
/// of 2026-11-08: one password shared by the demo accounts, read from the server's configuration,
/// accounts active at once, no activation e-mail). <c>CreateUserCommand</c> cannot do it: it always
/// mails an activation link, and an administrator's reset expires the password on the spot.
///
/// <para>
/// Guarded where it acts, not only by its caller: refused outside a test institution, refused on
/// a « Mode test » twin (its team is production's), refused for an address the demo template
/// (<see cref="DemoDataOptions.EmailTemplate"/>, owner's choice: <c>contact+{profil}.{institution}@talisoft.net</c>,
/// a mailbox of the publisher) does not produce — so it can never be pointed at a stranger. No
/// endpoint sends it; only the demo's starting data does.
/// </para>
/// </summary>
internal sealed record ProvisionDemoUserCommand(
    Guid AgencyId,
    string RoleCode,
    string FirstName,
    string LastName,
    string Email,
    [property: SensitiveData] string Password,
    Guid CreatedBy) : IRequest<Result<Guid>>, ICommand;

internal static class DemoUserErrors
{
    public const string NotTestTenant = "DEMO_USER_REQUIRES_TEST_TENANT";
    public const string NotDemoAddress = "DEMO_USER_ADDRESS_NOT_DEMO";
    public const string Exists = "DEMO_USER_EXISTS";
    /// <summary>A sign-in name is unique across every tenant: another institution already has this address.</summary>
    public const string TakenElsewhere = "DEMO_USER_ADDRESS_TAKEN_BY_ANOTHER_INSTITUTION";
    public const string AgencyNotFound = "AGENCY_NOT_FOUND";
    public const string RoleNotFound = "ROLE_NOT_FOUND";
}

internal sealed class ProvisionDemoUserHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    ITenantContext tenantContext,
    ITenantStore tenantStore,
    IOptions<DemoDataOptions> demoOptions,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    EmployeeNumberAllocator employeeNumbers) : IRequestHandler<ProvisionDemoUserCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ProvisionDemoUserCommand request, CancellationToken ct)
    {
        var tenant = await tenantStore.GetAsync(tenantContext.CurrentTenantId, ct);
        if (tenant is not { IsTest: true, ParentTenantId: null })
            return Result.Fail<Guid>(DemoUserErrors.NotTestTenant);
        if (!demoOptions.Value.IsDemoAddress(request.Email))
            return Result.Fail<Guid>(DemoUserErrors.NotDemoAddress);

        var tenantId = tenantContext.TeamTenantId;
        if (!await db.Agencies.AnyAsync(a => a.Id == request.AgencyId, ct))
            return Result.Fail<Guid>(DemoUserErrors.AgencyNotFound);
        var role = await roleManager.FindByNameAsync(request.RoleCode);
        if (role is null) return Result.Fail<Guid>(DemoUserErrors.RoleNotFound);

        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();
        var holders = await db.Users.IgnoreQueryFilters()
            .Where(u => u.NormalizedEmail == normalized || u.NormalizedUserName == normalized)
            .Select(u => u.TenantId)
            .ToListAsync(ct);
        if (holders.Contains(tenantId)) return Result.Fail<Guid>(DemoUserErrors.Exists);
        // Identity's user-name index spans every tenant: say so, rather than let it answer 23505.
        if (holders.Count > 0) return Result.Fail<Guid>(DemoUserErrors.TakenElsewhere);

        var user = AppUser.Create(tenantId, request.AgencyId, request.FirstName, request.LastName, email);
        user.RecordCreator(request.CreatedBy);
        user.SetEmployeeNumber(await employeeNumbers.NextAsync(tenantId, ct));
        user.Activate();

        // Through Identity, so the password policy applies and the hash is the usual one.
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            return Result.Fail<Guid>(string.Join("; ", created.Errors.Select(e => e.Description)));
        var inRole = await userManager.AddToRoleAsync(user, role.Name!);
        if (!inRole.Succeeded)
            return Result.Fail<Guid>(string.Join("; ", inRole.Errors.Select(e => e.Description)));

        await db.AddAsync(UserRole.Assign(tenantId, user.Id, role.Id, request.CreatedBy), ct);
        await db.AddAsync(UserProfile.Create(tenantId, user.Id), ct);
        db.PasswordHistories.Add(PasswordHistory.Create(tenantId, user.Id, user.PasswordHash!));
        await db.SaveChangesAsync(ct);

        await publisher.PublishAsync(new UserCreatedEvent(tenantId, user.Id, user.Email!, user.FullName), ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok(user.Id);
    }
}
