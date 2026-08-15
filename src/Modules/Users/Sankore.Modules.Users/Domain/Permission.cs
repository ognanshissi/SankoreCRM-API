namespace Sankore.Modules.Users.Domain;

public class Permission
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } =  string.Empty; // users:create, users:update, leads:dispatch
    public string Description { get; private set; } =  string.Empty;
    public string Module { get; private set; } =  string.Empty; // M01, M12
    public string Action { get; private set; } =  string.Empty; // Action (read, approve, download, delete, review, validate)
    
    private  Permission() {}


    public static Permission Create(string code, string description, string module, string action)
    {
        return new Permission()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = description,
            Module = module,
            Action = action
        };
    }
}