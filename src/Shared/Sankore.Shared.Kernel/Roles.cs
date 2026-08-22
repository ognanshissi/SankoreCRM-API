namespace Sankore.Shared.Kernel;


public record RoleItem(string Code, string Name);

public static class Roles
{
    public static readonly RoleItem System = new RoleItem("System", "Compte technique");
    public static readonly RoleItem Agent = new RoleItem("Agent", "Compte Agent");
    public static readonly RoleItem Cashier =  new RoleItem("Cashier", "Compte Cashier");
    public static readonly RoleItem Administrator = new RoleItem("Administrator", "Compte Administrateur");
    public static readonly RoleItem SalesManager = new RoleItem("SalesManager", "Compte Sales Manager");
    public static readonly RoleItem BranchManager = new RoleItem("BranchManager", "Compte Branch Manager");
    public static readonly RoleItem CommercialAgent = new RoleItem("CommercialAgent", "Compte Commercial Agent");
    public static readonly RoleItem RegulationManager = new RoleItem("RegulationManager", "Compte Regulation Manager");

    public static readonly RoleItem[] All =
    [
        System,
        Agent,
        Administrator,
        SalesManager,
        BranchManager,
        CommercialAgent,
        Cashier,
        RegulationManager,
    ];
}