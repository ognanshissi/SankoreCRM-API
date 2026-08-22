namespace Sankore.Modules.Administration.Infrastructure;

public static class Roles
{
    public const string Agent = "Agent";
    public const string Cashier = "Cashier";
    public const string Administrator = "Administrator";
    public const string SalesManager = "SalesManager";
    public const string BranchManager = "BranchManager";
    public const string CommercialAgent = "CommercialAgent";
    public const string RegulationManager = "RegulationManager";

    public static readonly string[] All =
    [
        Agent,
        Administrator,
        SalesManager,
        BranchManager,
        CommercialAgent,
        Cashier,
        RegulationManager,
    ];
}