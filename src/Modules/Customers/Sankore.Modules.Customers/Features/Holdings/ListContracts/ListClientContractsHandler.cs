namespace Sankore.Modules.Customers.Features.Holdings.ListContracts;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Folds the client's holdings, recorded policies and core banking snapshot into one list.
///
/// <para>
/// <b>A back office line is folded into the holding it realises</b>, never listed beside it: a
/// policy through the subscription that produced it (<see cref="RecordedPolicy.HoldingId"/>), an
/// account or a loan through <see cref="ProductHolding.ExternalRef"/>, the reference the back
/// office answered with. What no holding accounts for — a policy subscribed at the insurer's
/// counter, an account opened at the bank — is listed on its own, read-only.
/// </para>
///
/// <para>
/// <b>Each back office is read only when the caller may read it and the tenant has its module</b>
/// (readable: in force, or deactivated and kept for consultation), with the same permissions as
/// the screens that showed them before. A read that fails leaves the holdings standing and names
/// the source in <see cref="ClientContractsDto.UnavailableSources"/>: an insurer's outage must not
/// blank the client's contracts, and must not pass for a client who holds nothing there either.
/// </para>
/// </summary>
internal sealed class ListClientContractsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IAdministrationModule administration,
    ITenantModules modules,
    ILogger<ListClientContractsHandler> logger,
    IIntegrationModule? integration = null)
    : IRequestHandler<ListClientContractsQuery, Result<ClientContractsDto>>
{
    internal const string InsurerSource = "Insurer";
    internal const string CoreBankingSource = "CoreBanking";

    public async Task<Result<ClientContractsDto>> Handle(
        ListClientContractsQuery query, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == query.ClientId)
            .Select(c => new { c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientContractsDto>(CustomerErrors.ClientNotFound);

        var holdings = await db.ProductHoldings
            .Include(h => h.Designations)
            .Where(h => h.ClientId == query.ClientId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        var unavailable = new List<string>();
        var labels = await ProductLabelsAsync(holdings, ct);
        var policies = await PoliciesAsync(query.ClientId, unavailable, ct);
        var snapshot = await SnapshotAsync(query.ClientId, unavailable, ct);

        var items = new List<ClientContractDto>();
        var usedPolicies = new HashSet<Guid>();
        var usedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedLoans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var holding in holdings)
        {
            // Every policy of the holding is accounted for; the newest is the one shown.
            var linked = policies.Where(p => p.HoldingId == holding.Id).ToList();
            foreach (var p in linked) usedPolicies.Add(p.PolicyId);
            var policy = linked.OrderByDescending(p => p.EffectiveDate).FirstOrDefault();

            var reference = holding.ExternalRef?.Trim();
            var account = reference is null ? null : snapshot?.Accounts.FirstOrDefault(a =>
                string.Equals(a.AccountId.Value, reference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.AccountNumber, reference, StringComparison.OrdinalIgnoreCase));
            var loan = reference is null || account is not null ? null : snapshot?.Loans.FirstOrDefault(l =>
                string.Equals(l.LoanId.Value, reference, StringComparison.OrdinalIgnoreCase));

            if (account is not null) usedAccounts.Add(account.AccountId.Value);
            if (loan is not null) usedLoans.Add(loan.LoanId.Value);

            items.Add(FromHolding(holding, labels, policy, account, loan));
        }

        items.AddRange(policies
            .Where(p => !usedPolicies.Contains(p.PolicyId))
            .Select(FromPolicy));

        if (snapshot is not null)
        {
            items.AddRange(snapshot.Accounts
                .Where(a => !usedAccounts.Contains(a.AccountId.Value))
                .Select(FromAccount));
            items.AddRange(snapshot.Loans
                .Where(l => !usedLoans.Contains(l.LoanId.Value))
                .Select(FromLoan));
        }

        return Result.Ok(new ClientContractsDto(items, unavailable, snapshot?.SnapshotAt));
    }

    // ── sources ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<string, string>> ProductLabelsAsync(
        IReadOnlyList<ProductHolding> holdings, CancellationToken ct)
    {
        if (holdings.Count == 0)
            return new Dictionary<string, string>();

        // A missing label is the product code on screen, not a missing contract.
        try
        {
            return (await administration.ListProductsAsync(currentUser.TenantId, null, ct))
                .GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Client contracts: product names could not be resolved");
            return new Dictionary<string, string>();
        }
    }

    private async Task<IReadOnlyList<RecordedPolicy>> PoliciesAsync(
        Guid clientId, List<string> unavailable, CancellationToken ct)
    {
        if (integration is null
            || !Holds(Permissions.CanViewInsurancePolicy)
            || !await modules.IsReadableAsync(TenantModules.Insurance, ct))
            return [];

        try
        {
            return await integration.Insurance.ListRecordedPoliciesAsync(clientId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Client contracts: the recorded policies of {ClientId} could not be read", clientId);
            unavailable.Add(InsurerSource);
            return [];
        }
    }

    private async Task<CbsCustomerSnapshot?> SnapshotAsync(
        Guid clientId, List<string> unavailable, CancellationToken ct)
    {
        // The two codes the former « Core banking » tab accepted: either one let it show accounts.
        if (integration is null
            || !(Holds(Permissions.CanViewLiveBalance) || Holds(Permissions.CanViewIntegrationCommand))
            || !await modules.IsReadableAsync(TenantModules.BankingMicrofinance, ct))
            return null;

        try
        {
            // Null is « never synchronised », an ordinary state: no line, and nothing unavailable.
            return await integration.CoreBanking.GetCustomerSnapshotAsync(clientId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Client contracts: the core banking snapshot of {ClientId} could not be read", clientId);
            unavailable.Add(CoreBankingSource);
            return null;
        }
    }

    private bool Holds(PermissionItem permission)
        => currentUser.Permissions.Contains(permission.Code, StringComparer.Ordinal);

    // ── lines ───────────────────────────────────────────────────────────────

    private static ClientContractDto FromHolding(
        ProductHolding holding,
        IReadOnlyDictionary<string, string> labels,
        RecordedPolicy? policy,
        CbsAccount? account,
        CbsLoan? loan)
    {
        var terms = holding.Specifics;
        var (amount, kind, currency) = policy is not null ? (policy.PremiumAmount, "Premium", policy.Currency)
            : account is not null ? (account.Balance, "Balance", account.Currency)
            : loan is not null ? (loan.OutstandingAmount, "Outstanding", loan.Currency)
            : FromTerms(terms);

        return new ClientContractDto(
            Key: $"holding:{holding.Id}",
            Source: "Crm",
            Nature: NatureOf(holding.ProductCategory),
            Label: labels.TryGetValue(holding.ProductCode, out var name) ? name : holding.ProductCode,
            ProductCode: holding.ProductCode,
            Category: holding.ProductCategory,
            Status: holding.Status.ToString(),
            ExternalStatus: policy?.Status.ToString() ?? account?.Status ?? loan?.Status,
            Reference: policy?.PolicyNumber ?? account?.AccountNumber ?? holding.ExternalRef,
            Amount: amount,
            AmountKind: kind,
            Currency: currency,
            StartDate: policy?.EffectiveDate ?? account?.OpenedOn ?? loan?.DisbursedOn ?? terms.EffectiveFrom,
            HoldingId: holding.Id,
            PolicyId: policy?.PolicyId,
            AccountRef: account?.AccountId.Value,
            LoanRef: loan?.LoanId.Value,
            Insurer: policy?.Insurer,
            PremiumOutstanding: policy?.PremiumOutstanding ?? false,
            DesignationsToReview: holding.CurrentDesignations.Count(d => d.Status == DesignationStatus.ToReview),
            SignedAt: holding.SignedAt);
    }

    /// <summary>The figure the agent typed, before any back office answered.</summary>
    private static (decimal? Amount, string? Kind, string? Currency) FromTerms(HoldingSpecifics terms)
        => terms.Premium is { } premium ? (premium, "Premium", terms.Currency)
         : terms.InitialDeposit is { } deposit ? (deposit, "Deposit", terms.Currency)
         : terms.InsuredCapital is { } capital ? (capital, "InsuredCapital", terms.Currency)
         : (null, null, null);

    private static ClientContractDto FromPolicy(RecordedPolicy policy) => new(
        Key: $"policy:{policy.PolicyId}",
        Source: InsurerSource,
        Nature: "Insurance",
        Label: policy.ProductName,
        ProductCode: null,
        Category: null,
        Status: policy.Status.ToString(),
        ExternalStatus: null,
        Reference: policy.PolicyNumber,
        Amount: policy.PremiumAmount,
        AmountKind: "Premium",
        Currency: policy.Currency,
        StartDate: policy.EffectiveDate,
        HoldingId: null,
        PolicyId: policy.PolicyId,
        AccountRef: null,
        LoanRef: null,
        Insurer: policy.Insurer,
        PremiumOutstanding: policy.PremiumOutstanding);

    private static ClientContractDto FromAccount(CbsAccount account) => new(
        Key: $"account:{account.AccountId.Value}",
        Source: CoreBankingSource,
        Nature: "Account",
        Label: account.ProductLabel ?? account.ProductCode ?? "Compte",
        ProductCode: account.ProductCode,
        Category: null,
        Status: account.Status,
        ExternalStatus: null,
        Reference: account.AccountNumber,
        Amount: account.Balance,
        AmountKind: "Balance",
        Currency: account.Currency,
        StartDate: account.OpenedOn,
        HoldingId: null,
        PolicyId: null,
        AccountRef: account.AccountId.Value,
        LoanRef: null,
        Insurer: null,
        PremiumOutstanding: false);

    private static ClientContractDto FromLoan(CbsLoan loan) => new(
        Key: $"loan:{loan.LoanId.Value}",
        Source: CoreBankingSource,
        Nature: "Loan",
        Label: loan.ProductCode ?? "Prêt",
        ProductCode: loan.ProductCode,
        Category: null,
        Status: loan.Status,
        ExternalStatus: null,
        Reference: loan.LoanId.Value,
        Amount: loan.OutstandingAmount,
        AmountKind: "Outstanding",
        Currency: loan.Currency,
        StartDate: loan.DisbursedOn,
        HoldingId: null,
        PolicyId: null,
        AccountRef: null,
        LoanRef: loan.LoanId.Value,
        Insurer: null,
        PremiumOutstanding: false);

    /// <summary>What the line is, for the screen to pick its detail — from the category snapshot.</summary>
    internal static string NatureOf(string? category)
    {
        if (!Enum.TryParse<ProductCategory>(category, ignoreCase: true, out var parsed))
            return "Other";

        return parsed switch
        {
            _ when ProductCategories.IsInsurance(parsed) => "Insurance",
            ProductCategory.Loan or ProductCategory.GroupCredit or ProductCategory.Agriculture => "Loan",
            ProductCategory.Savings or ProductCategory.CurrentAccount or ProductCategory.Tontine => "Account",
            _ => "Other",
        };
    }
}
