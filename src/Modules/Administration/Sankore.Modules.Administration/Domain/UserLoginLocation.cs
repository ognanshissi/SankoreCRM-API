using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Domain;

public class UserLoginLocation
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    public GeoPoint? Location { get; private set; }

    public DateTimeOffset OccuredAt { get; private set; }

    private UserLoginLocation() { }

    public static UserLoginLocation Create(Guid tenantId, Guid userId, GeoPoint location)
    {
        return new UserLoginLocation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Location = location,
            OccuredAt = DateTimeOffset.Now
        };
    }
}
