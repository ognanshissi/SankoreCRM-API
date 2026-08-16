namespace Sankore.Modules.Users.Domain;

public class PasswordHistory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string PasswordHashed { get; private set; } =  string.Empty;
    public string PasswordSalt { get; private set; } =  string.Empty;
    public DateTime SetAt { get; private set; }
    
    private PasswordHistory() {}
}