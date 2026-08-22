namespace Sankore.Modules.Administration.Domain;

public class UserProfile
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public AppUser? User { get; private set; }
    public string DefaultLanguage { get; private set; } = "fr";

    private UserProfile() { }

    public static UserProfile Create(Guid tenantId, Guid userId, string defaultLanguage = "fr")
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            DefaultLanguage = defaultLanguage
        };
}