using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Domain;

public class Agency: AggregateRoot
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = String.Empty;
    public string Description { get; private set; } =  String.Empty;
    public Address? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsActive { get; set; }
    
    private Agency () {}
}