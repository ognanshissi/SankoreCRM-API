namespace Sankore.Modules.Users.Domain;

public class UserProfile
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public AppUser? User { get; private set; } = null;
    
    public string DefaultLanguage { get; private set; } = "fr";
}