using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public class UserProfile
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public AppUser? User { get; private set; }
    public string DefaultLanguage { get; private set; } = "fr";

    public Address Address { get; private set; } = null!;

    public PhoneNumber WorkNumber { get; private set; } = null!;

    public PhoneNumber HomeNumber { get; private set; } = null!;

    public PhoneNumber PersonalNumber { get; private set; } = null!;

    public string JobTitle { get; private set; } = null!; // The job title is different from the role. It is a string that describes the user's position in the company.
    
    public DateTimeOffset? BirthDate { get; private set; }
    
    public string AdditionalEmail  {get; private set; } = string.Empty;
    
    private UserProfile() { }

    public static UserProfile Create(Guid tenantId, Guid userId, string defaultLanguage = "fr")
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            DefaultLanguage = defaultLanguage,
            Address = new Address()
        };
}