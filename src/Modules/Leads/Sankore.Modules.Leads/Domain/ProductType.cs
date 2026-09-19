namespace Sankore.Modules.Leads.Domain;

/// <summary>The financial product category a <see cref="QualificationTemplate"/> is scoped to.</summary>
public enum ProductType
{
    /// <summary>Microcredit / loan products (Prêt).</summary>
    Loan,
    /// <summary>Savings account products (Épargne).</summary>
    Savings,
    /// <summary>Group solidarity credit (Crédit Groupe).</summary>
    GroupCredit,
    /// <summary>Rotating savings group (Tontine).</summary>
    Tontine,
    /// <summary>Agricultural financing products (Agriculture).</summary>
    Agriculture
}
